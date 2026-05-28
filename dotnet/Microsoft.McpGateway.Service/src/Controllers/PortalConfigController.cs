// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.McpGateway.Service.Controllers
{
    /// <summary>
    /// Serves the runtime configuration consumed by the management portal SPA.
    /// The portal calls this endpoint once on startup to decide whether to
    /// initialize MSAL (cloud mode) or fall back to the local development
    /// authentication handler.
    ///
    /// The endpoint is intentionally anonymous and only returns information
    /// that is also required to authenticate to the gateway (tenant id, client
    /// id, public origin). It never exposes secrets.
    /// </summary>
    [ApiController]
    [Route("portal/config")]
    [AllowAnonymous]
    public sealed class PortalConfigController(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<PortalConfigController> logger) : ControllerBase
    {
        private readonly IWebHostEnvironment _environment = environment;
        private readonly IConfiguration _configuration = configuration;
        private readonly ILogger<PortalConfigController> _logger = logger;

        // GET /portal/config
        [HttpGet]
        public IActionResult GetConfig()
        {
            try
            {
                // Treat the env-driven bypass the same as the Development
                // environment: the SPA skips MSAL and sends X-Dev-* headers
                // that the server's DevelopmentAuthenticationHandler reads.
                var bypassEntra = _configuration.GetValue<bool>("Authentication:BypassEntra");
                var isDevelopment = _environment.IsDevelopment() || bypassEntra;

                var azureAdSection = _configuration.GetSection("AzureAd");
                var tenantId = azureAdSection["TenantId"];
                var clientId = azureAdSection["ClientId"];
                var hasAzureAd = !isDevelopment
                    && !string.IsNullOrWhiteSpace(tenantId)
                    && !string.IsNullOrWhiteSpace(clientId)
                    // Don't leak the placeholder values shipped in appsettings.json.
                    && !string.Equals(tenantId, "YOUR_TENANT_ID", StringComparison.Ordinal)
                    && !string.Equals(clientId, "YOUR_API_CLIENT_ID", StringComparison.Ordinal);

                var publicOrigin = _configuration.GetValue<string>("PublicOrigin");
                if (string.IsNullOrWhiteSpace(publicOrigin))
                {
                    // Fall back to the host/scheme observed on this request so
                    // the SPA can still build redirect URIs in dev or in any
                    // setup that forgot to set PublicOrigin.
                    publicOrigin = $"{Request.Scheme}://{Request.Host.Value}";
                }

                var agentsEnabled = !string.IsNullOrWhiteSpace(_configuration["FoundrySettings:Endpoint"]);

                PortalAzureAd? azureAd = null;
                if (hasAzureAd)
                {
                    azureAd = new PortalAzureAd(
                        tenantId!,
                        clientId!,
                        new[] { $"api://{clientId}/.default" });
                }

                var payload = new PortalConfigResponse(
                    isDevelopment,
                    publicOrigin,
                    agentsEnabled,
                    azureAd);

                return Ok(payload);
            }
            catch (Exception ex)
            {
                // Surface the real reason in the body so an operator looking
                // at "Failed to load portal config (500 ...)" in the SPA gets
                // an actionable hint instead of a blank wall.
                _logger.LogError(ex, "Failed to build portal runtime config.");
                return Problem(
                    detail: ex.Message,
                    title: "Portal configuration failed",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }

        private sealed record PortalConfigResponse(
            bool IsDevelopment,
            string PublicOrigin,
            bool AgentsEnabled,
            PortalAzureAd? AzureAd);

        private sealed record PortalAzureAd(
            string TenantId,
            string ClientId,
            IReadOnlyList<string> Scopes);
    }
}
