// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
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
    }
}
