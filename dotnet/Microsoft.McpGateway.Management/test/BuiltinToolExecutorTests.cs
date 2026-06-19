// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Foundry;
using Moq;

namespace Microsoft.McpGateway.Management.Tests
{
    /// <summary>
    /// Regression tests for the built-in tool sandboxing: built-in bash runs
    /// with a minimal, sanitized environment plus a denylist guard.
    /// </summary>
    [TestClass]
    public class BuiltinToolExecutorTests
    {
        private readonly BuiltinToolExecutor _executor;
        private string _cwd = string.Empty;
        private readonly List<string> _externalPaths = new();

        public BuiltinToolExecutorTests()
        {
            _executor = new BuiltinToolExecutor(new Mock<ILogger<BuiltinToolExecutor>>().Object);
        }

        [TestInitialize]
        public void TestInitialize()
        {
            _cwd = Path.Combine(Path.GetTempPath(), "mcpgw-builtin-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_cwd);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try
            {
                if (Directory.Exists(_cwd))
                {
                    Directory.Delete(_cwd, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup; ignore.
            }

            foreach (var path in _externalPaths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // Best-effort cleanup; ignore.
                }
            }
        }

        [TestMethod]
        public void ApplySandboxedEnvironment_StripsNonAllowlistedVariables()
        {
            var psi = new ProcessStartInfo();
            psi.Environment["APP_CONFIG__SecretValue"] = "synthetic-1";
            psi.Environment["APP_CONFIG__Endpoint"] = "https://synthetic.example/";
            psi.Environment["SOME_OTHER_TOKEN"] = "synthetic-2";

            BuiltinToolExecutor.ApplySandboxedEnvironment(psi, _cwd);

            psi.Environment.Should().NotContainKey("APP_CONFIG__SecretValue");
            psi.Environment.Should().NotContainKey("APP_CONFIG__Endpoint");
            psi.Environment.Should().NotContainKey("SOME_OTHER_TOKEN");
        }

        [TestMethod]
        public void ApplySandboxedEnvironment_PinsHomeTmpAndPwdToWorkingDirectory()
        {
            var psi = new ProcessStartInfo();

            BuiltinToolExecutor.ApplySandboxedEnvironment(psi, _cwd);

            psi.Environment["HOME"].Should().Be(_cwd);
            psi.Environment["TMPDIR"].Should().Be(_cwd);
            psi.Environment["PWD"].Should().Be(_cwd);
        }

        [TestMethod]
        public void ApplySandboxedEnvironment_PreservesAllowlistedVariables()
        {
            var psi = new ProcessStartInfo();
            psi.Environment["LANG"] = "en_US.UTF-8";
            psi.Environment["TZ"] = "UTC";

            BuiltinToolExecutor.ApplySandboxedEnvironment(psi, _cwd);

            psi.Environment["LANG"].Should().Be("en_US.UTF-8");
            psi.Environment["TZ"].Should().Be("UTC");
        }

        [TestMethod]
        public void ApplySandboxedEnvironment_GuaranteesPathWhenMissing()
        {
            var psi = new ProcessStartInfo();
            psi.Environment.Clear();

            BuiltinToolExecutor.ApplySandboxedEnvironment(psi, _cwd);

            psi.Environment.Should().ContainKey("PATH");
            psi.Environment["PATH"].Should().NotBeNullOrEmpty();
        }

        [DataTestMethod]
        [DataRow("cat /proc/1/environ")]
        [DataRow("cat /proc/self/environ")]
        [DataRow("head -c 4096 /proc/1/cmdline")]
        [DataRow("cat /proc/self/mem")]
        public async Task ExecuteAsync_Bash_RejectsProcEnvironmentReads(string command)
        {
            var argsJson = System.Text.Json.JsonSerializer.Serialize(new { command });

            var result = await _executor.ExecuteAsync(BuiltinToolExecutor.Bash, argsJson, _cwd, CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("denylist");
        }

        [TestMethod]
        public async Task ExecuteAsync_Bash_PrintenvExposesOnlyAllowlistedVariables()
        {
            // This end-to-end check actually spawns /bin/bash, so it only runs
            // on Linux where the built-ins are designed to execute.
            if (!OperatingSystem.IsLinux() || !File.Exists("/bin/bash"))
            {
                Assert.Inconclusive("Requires Linux with /bin/bash to exercise the real bash built-in.");
            }

            const string variableName = "APP_CONFIG__SecretValue";
            const string variableValue = "SYNTHETIC_VALUE";
            Environment.SetEnvironmentVariable(variableName, variableValue);
            try
            {
                var result = await _executor.ExecuteAsync(
                    BuiltinToolExecutor.Bash,
                    "{\"command\":\"printenv | sort\"}",
                    _cwd,
                    CancellationToken.None);

                // The command runs and produces output (PATH is allowlisted)...
                result.Content.Should().Contain("PATH=");
                // ...but non-allowlisted variables must not appear in the output.
                result.Content.Should().NotContain(variableValue);
                result.Content.Should().NotContain(variableName);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variableName, null);
            }
        }

        // ---------------------------------------------------------------------
        // Path containment regression tests for builtin_read_file /
        // builtin_write_file (MSRC-122432: symlink traversal bypassing the
        // lexical path check).
        // ---------------------------------------------------------------------

        [TestMethod]
        public async Task ExecuteAsync_ReadFile_WithinSession_Succeeds()
        {
            await File.WriteAllTextAsync(Path.Combine(_cwd, "inside.txt"), "INSIDE");

            var result = await _executor.ExecuteAsync(
                BuiltinToolExecutor.ReadFile,
                Args(new { path = "inside.txt" }),
                _cwd,
                CancellationToken.None);

            result.IsError.Should().BeFalse();
            result.Content.Should().Contain("INSIDE");
        }

        [TestMethod]
        public async Task ExecuteAsync_WriteThenRead_WithinSession_RoundTrips()
        {
            var write = await _executor.ExecuteAsync(
                BuiltinToolExecutor.WriteFile,
                Args(new { path = "sub/created.txt", content = "ROUNDTRIP" }),
                _cwd,
                CancellationToken.None);
            write.IsError.Should().BeFalse();

            var read = await _executor.ExecuteAsync(
                BuiltinToolExecutor.ReadFile,
                Args(new { path = "sub/created.txt" }),
                _cwd,
                CancellationToken.None);

            read.IsError.Should().BeFalse();
            read.Content.Should().Contain("ROUNDTRIP");
        }

        [DataTestMethod]
        [DataRow("../outside.txt")]
        [DataRow("sub/../../outside.txt")]
        public async Task ExecuteAsync_ReadFile_RejectsDotDotTraversal(string path)
        {
            var result = await _executor.ExecuteAsync(
                BuiltinToolExecutor.ReadFile,
                Args(new { path }),
                _cwd,
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("traversal");
        }

        [TestMethod]
        public async Task ExecuteAsync_WriteFile_RejectsDotDotTraversal()
        {
            var result = await _executor.ExecuteAsync(
                BuiltinToolExecutor.WriteFile,
                Args(new { path = "../escape.txt", content = "x" }),
                _cwd,
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("traversal");
        }

        [DataTestMethod]
        [DataRow("/etc/passwd")]
        [DataRow("/tmp/abs.txt")]
        public async Task ExecuteAsync_ReadFile_RejectsAbsolutePaths(string path)
        {
            var result = await _executor.ExecuteAsync(
                BuiltinToolExecutor.ReadFile,
                Args(new { path }),
                _cwd,
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("Absolute paths");
        }

        [TestMethod]
        public async Task ExecuteAsync_WriteFile_RejectsAbsolutePaths()
        {
            var result = await _executor.ExecuteAsync(
                BuiltinToolExecutor.WriteFile,
                Args(new { path = "/tmp/abs-write.txt", content = "x" }),
                _cwd,
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("Absolute paths");
        }

        [TestMethod]
        public async Task ExecuteAsync_ReadFile_RejectsSymlinkDirectoryToOutside()
        {
            var external = NewExternalDirectory();
            await File.WriteAllTextAsync(Path.Combine(external, "secret.txt"), "EXTERNAL_SECRET");
            RequireSymlink(() => Directory.CreateSymbolicLink(Path.Combine(_cwd, "data_link"), external));

            var result = await _executor.ExecuteAsync(
                BuiltinToolExecutor.ReadFile,
                Args(new { path = "data_link/secret.txt" }),
                _cwd,
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("escapes the session working directory");
            result.Content.Should().NotContain("EXTERNAL_SECRET");
        }

        [TestMethod]
        public async Task ExecuteAsync_ReadFile_RejectsSymlinkFileToOutside()
        {
            var external = NewExternalDirectory();
            var externalFile = Path.Combine(external, "secret.txt");
            await File.WriteAllTextAsync(externalFile, "EXTERNAL_SECRET");
            RequireSymlink(() => File.CreateSymbolicLink(Path.Combine(_cwd, "leaf_link.txt"), externalFile));

            var result = await _executor.ExecuteAsync(
                BuiltinToolExecutor.ReadFile,
                Args(new { path = "leaf_link.txt" }),
                _cwd,
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("escapes the session working directory");
            result.Content.Should().NotContain("EXTERNAL_SECRET");
        }

        [TestMethod]
        public async Task ExecuteAsync_WriteFile_RejectsSymlinkDirectoryToOutside_AndDoesNotWrite()
        {
            var external = NewExternalDirectory();
            RequireSymlink(() => Directory.CreateSymbolicLink(Path.Combine(_cwd, "rw_link"), external));

            var result = await _executor.ExecuteAsync(
                BuiltinToolExecutor.WriteFile,
                Args(new { path = "rw_link/policy.json", content = "{\"state\":\"updated\"}" }),
                _cwd,
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("escapes the session working directory");
            File.Exists(Path.Combine(external, "policy.json")).Should().BeFalse("the write must not follow the symlink outside the session directory");
        }

        [TestMethod]
        public async Task ExecuteAsync_WriteFile_RejectsExistingSymlinkFileToOutside_AndDoesNotOverwrite()
        {
            var external = NewExternalDirectory();
            var externalFile = Path.Combine(external, "policy.json");
            await File.WriteAllTextAsync(externalFile, "{\"state\":\"baseline\"}");
            RequireSymlink(() => File.CreateSymbolicLink(Path.Combine(_cwd, "policy_link.json"), externalFile));

            var result = await _executor.ExecuteAsync(
                BuiltinToolExecutor.WriteFile,
                Args(new { path = "policy_link.json", content = "{\"state\":\"updated\"}" }),
                _cwd,
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("escapes the session working directory");
            (await File.ReadAllTextAsync(externalFile)).Should().Be("{\"state\":\"baseline\"}");
        }

        [TestMethod]
        public async Task ExecuteAsync_ReadFile_RejectsNestedSymlinkChainToOutside()
        {
            var external = NewExternalDirectory();
            await File.WriteAllTextAsync(Path.Combine(external, "secret.txt"), "EXTERNAL_SECRET");
            // a -> b -> external (chained links, both inside the session dir).
            RequireSymlink(() => Directory.CreateSymbolicLink(Path.Combine(_cwd, "b"), external));
            RequireSymlink(() => Directory.CreateSymbolicLink(Path.Combine(_cwd, "a"), Path.Combine(_cwd, "b")));

            var result = await _executor.ExecuteAsync(
                BuiltinToolExecutor.ReadFile,
                Args(new { path = "a/secret.txt" }),
                _cwd,
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("escapes the session working directory");
            result.Content.Should().NotContain("EXTERNAL_SECRET");
        }

        [TestMethod]
        public async Task ExecuteAsync_ReadFile_RejectsCrossSessionSymlink()
        {
            // A sibling "session" directory that shares the parent but is not a
            // descendant of _cwd. Also guards against the prefix-boundary bug
            // where "/root/sessA" would be considered inside "/root/sessA-x".
            var otherSession = NewExternalDirectory();
            await File.WriteAllTextAsync(Path.Combine(otherSession, "other.txt"), "OTHER_SESSION_DATA");
            RequireSymlink(() => Directory.CreateSymbolicLink(Path.Combine(_cwd, "peer"), otherSession));

            var result = await _executor.ExecuteAsync(
                BuiltinToolExecutor.ReadFile,
                Args(new { path = "peer/other.txt" }),
                _cwd,
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("escapes the session working directory");
            result.Content.Should().NotContain("OTHER_SESSION_DATA");
        }

        [TestMethod]
        public async Task ExecuteAsync_ReadFile_AllowsSymlinkResolvingInsideSession()
        {
            // A symlink that stays inside the session directory is still usable.
            Directory.CreateDirectory(Path.Combine(_cwd, "real"));
            await File.WriteAllTextAsync(Path.Combine(_cwd, "real", "ok.txt"), "INSIDE_OK");
            RequireSymlink(() => Directory.CreateSymbolicLink(Path.Combine(_cwd, "alias"), Path.Combine(_cwd, "real")));

            var result = await _executor.ExecuteAsync(
                BuiltinToolExecutor.ReadFile,
                Args(new { path = "alias/ok.txt" }),
                _cwd,
                CancellationToken.None);

            result.IsError.Should().BeFalse();
            result.Content.Should().Contain("INSIDE_OK");
        }

        private static string Args(object value) =>
            System.Text.Json.JsonSerializer.Serialize(value);

        private string NewExternalDirectory()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mcpgw-builtin-ext-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _externalPaths.Add(dir);
            return dir;
        }

        /// <summary>
        /// Runs a symlink-creating action, marking the test inconclusive when the
        /// host cannot create symbolic links (e.g. Windows without Developer Mode
        /// or the create-symlink privilege) instead of failing spuriously.
        /// </summary>
        private static void RequireSymlink(Action create)
        {
            try
            {
                create();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Assert.Inconclusive($"Symbolic link creation is not permitted on this host: {ex.Message}");
            }
        }
    }
}
