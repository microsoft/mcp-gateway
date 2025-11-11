// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Microsoft.McpGateway.Service.Authentication
{
    /// <summary>
    /// Authentication handler for development environments that issues a mock principal.
    /// Supports overriding the user id, display name, and roles via the optional
    /// `X-Dev-User`, `X-Dev-Name`, and `X-Dev-Roles` request headers when running locally.
    /// </summary>
    public sealed class DevelopmentAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Development";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var httpContext = Context;
            var request = httpContext.Request;

            var userId = request.Headers.TryGetValue("X-Dev-UserId", out var idValues)
                ? idValues.ToString().Trim()
                : null;

            var userName = request.Headers.TryGetValue("X-Dev-Name", out var nameValues)
                ? nameValues.ToString().Trim()
                : userId;

            var roleValues = request.Headers.TryGetValue("X-Dev-Roles", out var rolesHeader)
                ? rolesHeader.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
                : [];

            if (string.IsNullOrWhiteSpace(userId))
            {
                userId = "dev";
            }

            if (string.IsNullOrWhiteSpace(userName))
            {
                userName = userId;
            }

            var identity = new ClaimsIdentity(SchemeName);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId));
            identity.AddClaim(new Claim(ClaimTypes.Name, userName));

            if (roleValues.Length == 0)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, "mcp.dev"));
            }
            else
            {
                foreach (var role in roleValues)
                {
                    var trimmedRole = role.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmedRole))
                    {
                        identity.AddClaim(new Claim(ClaimTypes.Role, trimmedRole));
                    }
                }
            }

            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
