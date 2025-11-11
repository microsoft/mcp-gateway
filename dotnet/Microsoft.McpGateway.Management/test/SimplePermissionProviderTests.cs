// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using FluentAssertions;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Tests;

[TestClass]
public class SimplePermissionProviderTests
{
    private readonly SimplePermissionProvider _provider = new();

    [TestMethod]
    public async Task Creator_ShouldHaveFullAccess()
    {
        var principal = CreatePrincipal("owner");
        var adapter = CreatePermission("adapter", "owner");
        var tool = CreatePermission("tool", "owner");

        (await _provider.CheckAccessAsync(principal, adapter, Operation.Read)).Should().BeTrue();
        (await _provider.CheckAccessAsync(principal, adapter, Operation.Write)).Should().BeTrue();
        (await _provider.CheckAccessAsync(principal, tool, Operation.Read)).Should().BeTrue();
        (await _provider.CheckAccessAsync(principal, tool, Operation.Write)).Should().BeTrue();
    }

    [TestMethod]
    public async Task Admin_ShouldHaveFullAccess()
    {
        var principal = CreatePrincipal("user", "mcp.admin");
        var adapter = CreatePermission("adapter", "owner");
        var tool = CreatePermission("tool", "owner");

        (await _provider.CheckAccessAsync(principal, adapter, Operation.Read)).Should().BeTrue();
        (await _provider.CheckAccessAsync(principal, adapter, Operation.Write)).Should().BeTrue();
        (await _provider.CheckAccessAsync(principal, tool, Operation.Read)).Should().BeTrue();
        (await _provider.CheckAccessAsync(principal, tool, Operation.Write)).Should().BeTrue();
    }

    [TestMethod]
    public async Task RequiredRole_ShouldPermitRead()
    {
        var principal = CreatePrincipal("user", "reader");
        var adapter = CreatePermission("adapter", "owner", "reader");
        var tool = CreatePermission("tool", "owner", "reader");

        (await _provider.CheckAccessAsync(principal, adapter, Operation.Read)).Should().BeTrue();
        (await _provider.CheckAccessAsync(principal, tool, Operation.Read)).Should().BeTrue();
    }

    [TestMethod]
    public async Task NonRequiredRole_ShouldDenyRead()
    {
        var principal = CreatePrincipal("user", "guest");
        var adapter = CreatePermission("adapter", "owner", "reader");
        var tool = CreatePermission("tool", "owner", "reader");

        (await _provider.CheckAccessAsync(principal, adapter, Operation.Read)).Should().BeFalse();
        (await _provider.CheckAccessAsync(principal, tool, Operation.Read)).Should().BeFalse();
    }

    [TestMethod]
    public async Task RequiredRoles_ShouldNotGrantWrite()
    {
        var principal = CreatePrincipal("user", "editor");
        var adapter = CreatePermission("adapter", "owner", "editor");
        var tool = CreatePermission("tool", "owner", "editor");

        (await _provider.CheckAccessAsync(principal, adapter, Operation.Write)).Should().BeFalse();
        (await _provider.CheckAccessAsync(principal, tool, Operation.Write)).Should().BeFalse();
    }

    [TestMethod]
    public async Task EmptyRequiredRoles_ShouldPermitRead()
    {
        var principal = CreatePrincipal("user");
        var adapter = CreatePermission("adapter", "owner");
        adapter.RequiredRoles.Clear();
        var tool = CreatePermission("tool", "owner");
        tool.RequiredRoles.Clear();

        (await _provider.CheckAccessAsync(principal, adapter, Operation.Read)).Should().BeTrue();
        (await _provider.CheckAccessAsync(principal, tool, Operation.Read)).Should().BeTrue();
    }

    [TestMethod]
    public async Task CheckAccessAsync_ShouldFilterCollection()
    {
        var principal = CreatePrincipal("user", "reader");
        var allowed = CreatePermission("adapter1", "owner", "reader");
        var denied = CreatePermission("adapter2", "owner", "other");

        var result = await _provider.CheckAccessAsync(principal, new[] { allowed, denied }, Operation.Read);

        result.Should().ContainSingle(r => ReferenceEquals(r, allowed));
    }

    private static ClaimsPrincipal CreatePrincipal(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ResourcePermission CreatePermission(string resourceId, string owner, params string[] RequiredRoles) => new()
    {
        ResourceId = resourceId,
        Owner = owner,
        RequiredRoles = RequiredRoles.ToList()
    };
}
