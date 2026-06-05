// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Microsoft.McpGateway.Management.Foundry
{
    /// <summary>
    /// Built-in agent tools that run in-process inside the gateway pod.
    /// All file operations are confined to a per-session
    /// <c>workingDirectory</c>; bash commands inherit that as cwd and run with
    /// a sanitized, default-deny environment (see
    /// <see cref="ApplySandboxedEnvironment"/>). A regex denylist blocks obvious
    /// privileged / network / lateral-movement foot-guns as defense-in-depth.
    /// This is still not a full sandbox: for multi-tenant or production use,
    /// run built-ins in an out-of-process sandbox (gVisor / firejail /
    /// pod-per-session).
    /// </summary>
    public class BuiltinToolExecutor
    {
        public const string Bash = "builtin_bash";
        public const string ReadFile = "builtin_read_file";
        public const string WriteFile = "builtin_write_file";

        public static readonly IReadOnlyList<string> SupportedKinds = new[] { Bash, ReadFile, WriteFile };

        private const int DefaultBashTimeoutSeconds = 30;
        private const int MaxBashTimeoutSeconds = 120;
        private const int MaxOutputBytes = 16 * 1024; // 16 KiB per stream
        private const int MaxFileBytes = 256 * 1024;  // 256 KiB read/write cap
        private const long DefaultSessionDiskQuotaBytes = 4 * 1024 * 1024; // 4 MiB total writes per session

        // Bash command denylist. Matched against the raw command string
        // case-insensitively as whole words. Intentionally narrow (block clear
        // foot-guns / lateral-movement) rather than allowlist (which would
        // make the tool useless for legitimate dev work).
        // P3+ should replace this with a real sandbox (gVisor / firejail /
        // pod-per-session) and demote this to defense-in-depth.
        private static readonly Regex DenyPattern = new(
            pattern: @"\b(?:sudo|su|mount|umount|kill|pkill|killall|reboot|shutdown|halt|poweroff|init|systemctl|service|iptables|nft|nftables|ip6tables|ufw|firewall-cmd|chroot|insmod|rmmod|modprobe|sysctl|dd|mkfs(?:\.\w+)?|fdisk|parted|crontab|at|chage|useradd|userdel|usermod|groupadd|passwd|visudo|setcap|setfacl|chown|chmod\s+[0-7]*[2367]?7+|nc|ncat|netcat|socat|nmap|ssh|scp|sftp|rsync|telnet|ftp|tftp|curl|wget|aria2c|http(?:ie)?)\b|/etc/(?:passwd|shadow|sudoers|hosts)|/proc/(?:sys|kcore|[^/\s]+/(?:mem|environ|cmdline))|/sys/(?:kernel|class)|\$\(.*?(?:curl|wget|nc|sh|bash|eval|exec).*?\)|`.*?(?:curl|wget|nc|sh|bash|eval|exec).*?`|>\s*/dev/(?!null|stdout|stderr)|rm\s+-[a-zA-Z]*r[a-zA-Z]*f?\s+/(?!tmp)",
            options: RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Environment variables that are safe to expose.
        private static readonly IReadOnlyList<string> AllowedEnvironmentVariables = new[]
        {
            "PATH",
            "LANG",
            "LC_ALL",
            "LC_CTYPE",
            "TERM",
            "TZ",
        };

        private const string FallbackPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

        // Tracks bytes written per session for soft disk-quota enforcement.
        // In-process (not durable across restarts); fine for single-replica
        // gateway and as best-effort backstop in front of pod ephemeral disk.
        private readonly ConcurrentDictionary<string, long> _sessionBytesWritten = new();

        private readonly ILogger<BuiltinToolExecutor> _logger;

        public BuiltinToolExecutor(ILogger<BuiltinToolExecutor> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<ToolResult> ExecuteAsync(string kind, string argumentsJson, string workingDirectory, CancellationToken cancellationToken)
        {
            EnsureWorkingDirectory(workingDirectory);
            using var doc = ParseArgs(argumentsJson, out var parseError);
            if (parseError != null)
            {
                return Task.FromResult(Error(parseError));
            }
            var root = doc!.RootElement;
            return kind switch
            {
                Bash => RunBashAsync(root, workingDirectory, cancellationToken),
                ReadFile => Task.FromResult(RunReadFile(root, workingDirectory)),
                WriteFile => Task.FromResult(RunWriteFile(root, workingDirectory)),
                _ => Task.FromResult(Error($"Unknown builtin kind '{kind}'.")),
            };
        }

        public static global::OpenAI.Chat.ChatTool BuildChatTool(string kind)
        {
            return kind switch
            {
                Bash => global::OpenAI.Chat.ChatTool.CreateFunctionTool(
                    functionName: Bash,
                    functionDescription: "Run a shell command in the session's working directory. Output is captured (stdout, stderr, exit code).",
                    functionParameters: BinaryData.FromString("""
                        {"type":"object","properties":{
                          "command":{"type":"string","description":"Shell command to execute via /bin/bash -c."},
                          "timeout_seconds":{"type":"integer","description":"Optional timeout (1-120, default 30)."}
                        },"required":["command"]}
                        """)),
                ReadFile => global::OpenAI.Chat.ChatTool.CreateFunctionTool(
                    functionName: ReadFile,
                    functionDescription: "Read a UTF-8 text file relative to the session's working directory.",
                    functionParameters: BinaryData.FromString("""
                        {"type":"object","properties":{
                          "path":{"type":"string","description":"Path relative to the session working directory. Absolute paths and '..' segments are rejected."}
                        },"required":["path"]}
                        """)),
                WriteFile => global::OpenAI.Chat.ChatTool.CreateFunctionTool(
                    functionName: WriteFile,
                    functionDescription: "Write (or overwrite) a UTF-8 text file relative to the session's working directory.",
                    functionParameters: BinaryData.FromString("""
                        {"type":"object","properties":{
                          "path":{"type":"string","description":"Path relative to the session working directory."},
                          "content":{"type":"string","description":"File content (UTF-8)."}
                        },"required":["path","content"]}
                        """)),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown builtin tool kind."),
            };
        }

        private async Task<ToolResult> RunBashAsync(JsonElement args, string cwd, CancellationToken cancellationToken)
        {
            if (!args.TryGetProperty("command", out var cmdProp) || cmdProp.ValueKind != JsonValueKind.String)
            {
                return Error("'command' string argument is required.");
            }
            var command = cmdProp.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
            {
                return Error("'command' must be non-empty.");
            }
            if (DenyPattern.IsMatch(command))
            {
                _logger.LogWarning("Rejected bash command (matched denylist): {cmd}", command.Length > 200 ? command[..200] + "..." : command);
                return Error("Command rejected by sandbox policy (matched denylist for privileged / network / lateral-movement operations). Stick to local file and computation tasks within the session working directory.");
            }
            var timeoutSec = DefaultBashTimeoutSeconds;
            if (args.TryGetProperty("timeout_seconds", out var toProp) && toProp.ValueKind == JsonValueKind.Number && toProp.TryGetInt32(out var t))
            {
                timeoutSec = Math.Clamp(t, 1, MaxBashTimeoutSeconds);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                ArgumentList = { "-c", command },
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            ApplySandboxedEnvironment(psi, cwd);

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stdoutBuf = new StringBuilder();
            var stderrBuf = new StringBuilder();
            proc.OutputDataReceived += (_, e) => Append(stdoutBuf, e.Data);
            proc.ErrorDataReceived += (_, e) => Append(stderrBuf, e.Data);

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start bash process.");
                return Error($"Failed to start bash: {ex.Message}");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            bool timedOut = false;
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                try { await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
            }

            var payload = new
            {
                exitCode = timedOut ? -1 : proc.ExitCode,
                timedOut,
                stdout = stdoutBuf.ToString(),
                stderr = stderrBuf.ToString(),
            };
            var body = JsonSerializer.Serialize(payload);
            var isError = timedOut || payload.exitCode != 0;
            _logger.LogInformation("bash exit={code} timedOut={to} stdout={out}B stderr={err}B",
                payload.exitCode, timedOut, stdoutBuf.Length, stderrBuf.Length);
            return new ToolResult(body, isError);
        }

        private ToolResult RunReadFile(JsonElement args, string cwd)
        {
            if (!args.TryGetProperty("path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String)
            {
                return Error("'path' string argument is required.");
            }
            var (full, pathError) = ResolvePath(cwd, pathProp.GetString() ?? string.Empty);
            if (pathError != null)
            {
                return Error(pathError);
            }
            if (!File.Exists(full))
            {
                return Error($"File not found: {Path.GetRelativePath(cwd, full)}");
            }
            try
            {
                var info = new FileInfo(full);
                if (info.Length > MaxFileBytes)
                {
                    return Error($"File too large to read ({info.Length} bytes; max {MaxFileBytes}).");
                }
                var content = File.ReadAllText(full, Encoding.UTF8);
                return new ToolResult(JsonSerializer.Serialize(new { path = Path.GetRelativePath(cwd, full), content }), IsError: false);
            }
            catch (Exception ex)
            {
                return Error($"Read failed: {ex.Message}");
            }
        }

        private ToolResult RunWriteFile(JsonElement args, string cwd)
        {
            if (!args.TryGetProperty("path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String)
            {
                return Error("'path' string argument is required.");
            }
            if (!args.TryGetProperty("content", out var contentProp) || contentProp.ValueKind != JsonValueKind.String)
            {
                return Error("'content' string argument is required.");
            }
            var content = contentProp.GetString() ?? string.Empty;
            var byteCount = Encoding.UTF8.GetByteCount(content);
            if (byteCount > MaxFileBytes)
            {
                return Error($"Content too large ({byteCount} bytes; max {MaxFileBytes}).");
            }
            // Validate the path BEFORE charging the quota so a malformed/unsafe
            // path can't push the running total up and starve subsequent writes.
            var (full, pathError) = ResolvePath(cwd, pathProp.GetString() ?? string.Empty);
            if (pathError != null)
            {
                return Error(pathError);
            }
            // Soft disk quota: track total bytes written per session.
            var quotaKey = cwd;
            var newTotal = _sessionBytesWritten.AddOrUpdate(quotaKey, byteCount, (_, prev) => prev + byteCount);
            if (newTotal > DefaultSessionDiskQuotaBytes)
            {
                // Roll back the count so a smaller subsequent write can still succeed.
                _sessionBytesWritten.AddOrUpdate(quotaKey, 0, (_, prev) => Math.Max(0, prev - byteCount));
                return Error($"Session disk quota exceeded ({newTotal} bytes; max {DefaultSessionDiskQuotaBytes}).");
            }
            bool committed = false;
            try
            {
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                committed = true;
                var info = new FileInfo(full);
                return new ToolResult(JsonSerializer.Serialize(new { path = Path.GetRelativePath(cwd, full), bytesWritten = info.Length }), IsError: false);
            }
            catch (Exception ex)
            {
                return Error($"Write failed: {ex.Message}");
            }
            finally
            {
                // If the write threw before completing, refund the quota so the
                // failure doesn't permanently consume the session's budget.
                if (!committed)
                {
                    _sessionBytesWritten.AddOrUpdate(quotaKey, 0, (_, prev) => Math.Max(0, prev - byteCount));
                }
            }
        }

        /// <summary>
        /// Drop any per-session bookkeeping (currently the disk-quota counter)
        /// for the given working directory. Should be called when the owning
        /// session is deleted so long-running pods don't accumulate orphaned
        /// quota entries. Idempotent.
        /// </summary>
        public void ReleaseSession(string workingDirectory)
        {
            if (string.IsNullOrEmpty(workingDirectory))
            {
                return;
            }
            _sessionBytesWritten.TryRemove(workingDirectory, out _);
        }

        private static (string fullPath, string? error) ResolvePath(string cwd, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                return (string.Empty, "'path' must be non-empty.");
            }
            if (Path.IsPathRooted(relative))
            {
                return (string.Empty, "Absolute paths are not allowed.");
            }
            if (relative.Split('/', '\\').Any(seg => seg == ".."))
            {
                return (string.Empty, "Path traversal ('..') is not allowed.");
            }
            var combined = Path.GetFullPath(Path.Combine(cwd, relative));
            var cwdFull = Path.GetFullPath(cwd);
            if (!combined.StartsWith(cwdFull, StringComparison.Ordinal))
            {
                return (string.Empty, "Path escapes the session working directory.");
            }
            return (combined, null);
        }

        private void Append(StringBuilder buf, string? data)
        {
            if (data == null) return;
            lock (buf)
            {
                if (buf.Length >= MaxOutputBytes) return;
                var remaining = MaxOutputBytes - buf.Length;
                if (data.Length + 1 > remaining)
                {
                    buf.Append(data, 0, Math.Min(data.Length, remaining));
                    if (buf.Length < MaxOutputBytes)
                    {
                        buf.Append('\n');
                    }
                }
                else
                {
                    buf.AppendLine(data);
                }
            }
        }

        /// <summary>
        /// Replace the child process environment with a minimal, non-sensitive
        /// allowlist so built-in shell commands only see a small, well-known
        /// set of variables instead of the full gateway process environment.
        /// </summary>
        internal static void ApplySandboxedEnvironment(ProcessStartInfo psi, string workingDirectory)
        {
            // Accessing psi.Environment lazily seeds it with the parent
            // process environment; capture the values we want to keep before
            // clearing everything else.
            var preserved = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var name in AllowedEnvironmentVariables)
            {
                if (psi.Environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
                {
                    preserved[name] = value;
                }
            }

            psi.Environment.Clear();

            foreach (var kvp in preserved)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }

            // Guarantee a usable PATH even if the gateway process had none.
            if (!psi.Environment.ContainsKey("PATH"))
            {
                psi.Environment["PATH"] = FallbackPath;
            }

            // Confine HOME/TMPDIR/PWD to the session working directory.
            psi.Environment["HOME"] = workingDirectory;
            psi.Environment["TMPDIR"] = workingDirectory;
            psi.Environment["PWD"] = workingDirectory;
        }

        private static void EnsureWorkingDirectory(string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                throw new InvalidOperationException("Built-in tools require a session WorkingDirectory.");
            }
            if (!Directory.Exists(workingDirectory))
            {
                Directory.CreateDirectory(workingDirectory);
            }
        }

        private static JsonDocument? ParseArgs(string argumentsJson, out string? error)
        {
            error = null;
            try
            {
                return JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            }
            catch (JsonException ex)
            {
                error = $"Invalid JSON arguments: {ex.Message}";
                return null;
            }
        }

        private static ToolResult Error(string message) =>
            new(JsonSerializer.Serialize(new { error = message }), IsError: true);
    }
}
