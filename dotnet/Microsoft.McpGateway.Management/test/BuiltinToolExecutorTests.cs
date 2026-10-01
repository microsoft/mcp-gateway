// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
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
    /// Built-in tools must remain disabled without process or file side effects.
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

        [DataTestMethod]
        [DataRow(BuiltinToolExecutor.Bash, "{\"command\":\"printf executed > marker.txt\"}")]
        [DataRow(BuiltinToolExecutor.Bash, "{\"command\":\"python3 -c \\\"print('disabled')\\\"\"}")]
        [DataRow(BuiltinToolExecutor.ReadFile, "{\"path\":\"marker.txt\"}")]
        [DataRow(BuiltinToolExecutor.WriteFile, "{\"path\":\"marker.txt\",\"content\":\"executed\"}")]
        [DataRow(BuiltinToolExecutor.Bash, "{")]
        public async Task ExecuteAsync_DisabledTools_RejectsBeforeCreatingWorkingDirectory(string kind, string argumentsJson)
        {
            var workingDirectory = Path.Combine(_cwd, "disabled-session");

            var result = await _executor.ExecuteAsync(kind, argumentsJson, workingDirectory, CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain(BuiltinToolExecutor.DisabledMessage);
            Directory.Exists(workingDirectory).Should().BeFalse();
        }

        [DataTestMethod]
        [DataRow(BuiltinToolExecutor.Bash)]
        [DataRow(BuiltinToolExecutor.ReadFile)]
        [DataRow(BuiltinToolExecutor.WriteFile)]
        public async Task ExecuteAsync_DisabledTools_RejectsInvalidArgumentsAndWorkingDirectory(string kind)
        {
            var result = await _executor.ExecuteAsync(kind, "{", string.Empty, CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain(BuiltinToolExecutor.DisabledMessage);
        }

        [DataTestMethod]
        [DataRow(BuiltinToolExecutor.Bash)]
        [DataRow(BuiltinToolExecutor.ReadFile)]
        [DataRow(BuiltinToolExecutor.WriteFile)]
        public async Task ExecuteAsync_DisabledTools_DoesNotExposeOrOverwriteExistingFile(string kind)
        {
            const string originalContent = "SYNTHETIC_ORIGINAL_CONTENT";
            var path = Path.Combine(_cwd, "marker.txt");
            await File.WriteAllTextAsync(path, originalContent);
            var argumentsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                command = "printf overwritten > marker.txt",
                path = "marker.txt",
                content = "overwritten",
            });

            var result = await _executor.ExecuteAsync(kind, argumentsJson, _cwd, CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain(BuiltinToolExecutor.DisabledMessage);
            result.Content.Should().NotContain(originalContent);
            (await File.ReadAllTextAsync(path)).Should().Be(originalContent);
        }

        [DataTestMethod]
        [DataRow(BuiltinToolExecutor.Bash)]
        [DataRow(BuiltinToolExecutor.ReadFile)]
        [DataRow(BuiltinToolExecutor.WriteFile)]
        public void BuildChatTool_ShouldRejectDisabledBuiltin(string kind)
        {
            var act = () => BuiltinToolExecutor.BuildChatTool(kind);

            act.Should().Throw<NotSupportedException>().WithMessage(BuiltinToolExecutor.DisabledMessage);
        }

        [TestMethod]
        public void SupportedKinds_ShouldBeEmpty()
        {
            BuiltinToolExecutor.SupportedKinds.Should().BeEmpty();
        }
    }
}
