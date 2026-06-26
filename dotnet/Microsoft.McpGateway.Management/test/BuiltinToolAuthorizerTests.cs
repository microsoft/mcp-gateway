// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.McpGateway.Management.Authorization;

namespace Microsoft.McpGateway.Management.Tests
{
    /// <summary>
    /// Tests for the built-in tool capability gate: only callers holding
    /// <c>mcp.admin</c> or a configured role may use the privileged in-process
    /// built-ins, and there is no creator bypass.
    /// </summary>
    [TestClass]
    public class BuiltinToolAuthorizerTests
    {
        private static ClaimsPrincipal Caller(params string[] roles)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "user1") };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        private static BuiltinToolAuthorizer Create(params string[] requiredRoles) =>
            new(Options.Create(new BuiltinToolSettings { RequiredRoles = requiredRoles.ToList() }));

        [TestMethod]
        public void IsAuthorized_AllowsAdmin_WhenNoRolesConfigured()
        {
            Create().IsAuthorized(Caller("mcp.admin")).Should().BeTrue();
        }

        [TestMethod]
        public void IsAuthorized_AdminCheckIsCaseInsensitive()
        {
            Create().IsAuthorized(Caller("MCP.Admin")).Should().BeTrue();
        }

        [TestMethod]
        public void IsAuthorized_DeniesNonAdmin_WhenNoRolesConfigured()
        {
            Create().IsAuthorized(Caller("mcp.engineer")).Should().BeFalse();
        }

        [TestMethod]
        public void IsAuthorized_DeniesCaller_WithNoRoles()
        {
            Create().IsAuthorized(Caller()).Should().BeFalse();
        }

        [TestMethod]
        public void IsAuthorized_AllowsConfiguredRole()
        {
            Create("mcp.builtin").IsAuthorized(Caller("mcp.builtin")).Should().BeTrue();
        }

        [TestMethod]
        public void IsAuthorized_AllowsConfiguredRole_CaseInsensitive()
        {
            Create("mcp.builtin").IsAuthorized(Caller("MCP.Builtin")).Should().BeTrue();
        }

        [TestMethod]
        public void IsAuthorized_StillAllowsAdmin_WhenOtherRolesConfigured()
        {
            Create("mcp.builtin").IsAuthorized(Caller("mcp.admin")).Should().BeTrue();
        }

        [TestMethod]
        public void IsAuthorized_DeniesUnlistedRole()
        {
            Create("mcp.builtin").IsAuthorized(Caller("mcp.engineer")).Should().BeFalse();
        }

        [TestMethod]
        public void IsAuthorized_Throws_OnNullPrincipal()
        {
            var act = () => Create().IsAuthorized(null!);
            act.Should().Throw<ArgumentNullException>();
        }
    }
}
