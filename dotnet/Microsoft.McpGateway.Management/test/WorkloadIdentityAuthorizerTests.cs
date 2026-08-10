// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.McpGateway.Management.Authorization;

namespace Microsoft.McpGateway.Management.Tests
{
    /// <summary>
    /// Tests for the workload identity capability gate: only callers holding
    /// <c>mcp.admin</c> or a configured role may bind a deployment to the
    /// cluster's shared federated identity, and there is no creator bypass.
    /// </summary>
    [TestClass]
    public class WorkloadIdentityAuthorizerTests
    {
        private static ClaimsPrincipal Caller(params string[] roles)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "user1") };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        private static WorkloadIdentityAuthorizer Create(params string[] requiredRoles) =>
            new(Options.Create(new WorkloadIdentitySettings { RequiredRoles = requiredRoles.ToList() }));

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
            Create("mcp.workload").IsAuthorized(Caller("mcp.workload")).Should().BeTrue();
        }

        [TestMethod]
        public void IsAuthorized_AllowsConfiguredRole_CaseInsensitive()
        {
            Create("mcp.workload").IsAuthorized(Caller("MCP.Workload")).Should().BeTrue();
        }

        [TestMethod]
        public void IsAuthorized_StillAllowsAdmin_WhenOtherRolesConfigured()
        {
            Create("mcp.workload").IsAuthorized(Caller("mcp.admin")).Should().BeTrue();
        }

        [TestMethod]
        public void IsAuthorized_DeniesUnlistedRole()
        {
            Create("mcp.workload").IsAuthorized(Caller("mcp.engineer")).Should().BeFalse();
        }

        [TestMethod]
        public void IsAuthorized_Throws_OnNullPrincipal()
        {
            var act = () => Create().IsAuthorized(null!);

            act.Should().Throw<ArgumentNullException>();
        }

        [TestMethod]
        public void Constructor_Throws_OnNullOptions()
        {
            var act = () => new WorkloadIdentityAuthorizer(null!);

            act.Should().Throw<ArgumentNullException>();
        }
    }
}
