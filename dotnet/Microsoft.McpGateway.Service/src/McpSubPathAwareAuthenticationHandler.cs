// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Authentication;

namespace ModelContextProtocol.AspNetCore.Authentication;

/// <summary>
/// Authentication handler for MCP protocol that adds resource metadata to challenge responses
/// and handles resource metadata endpoint requests.
/// </summary>
public class McpSubPathAwareAuthenticationHandler : AuthenticationHandler<McpAuthenticationOptions>, IAuthenticationRequestHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="McpAuthenticationHandler"/> class.
    /// </summary>
    public McpSubPathAwareAuthenticationHandler(
        IOptionsMonitor<McpAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <inheritdoc />
    public async Task<bool> HandleRequestAsync()
    {
        // Check if the request is for the resource metadata endpoint
        string requestPath = Request.Path.Value ?? string.Empty;

        var metadataUri = Options.ResourceMetadataUri;
        if (metadataUri is null)
            return false;
        string expectedMetadataPath = metadataUri.IsAbsoluteUri ? metadataUri.AbsolutePath : metadataUri.OriginalString;

        // If the path doesn't match, let the request continue through the pipeline
        if (!new PathString(requestPath).StartsWithSegments(expectedMetadataPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return await HandleResourceMetadataRequestAsync();
    }

    /// <summary>
    /// Gets the base URL from the current request, including scheme, host, and path base.
    /// </summary>
    private string GetBaseUrl() => Uri.TryCreate(Options.ResourceMetadata?.Resource, UriKind.Absolute, out var resourceUri)
        ? resourceUri.GetLeftPart(UriPartial.Authority) + Request.PathBase
        : throw new InvalidOperationException("ResourceMetadata.Resource must configure the public origin.");

    private string GetCurrentPath() => Request.Path.HasValue ? Request.Path.Value! : string.Empty;

    /// <summary>
    /// Gets the absolute URI for the resource metadata endpoint.
    /// </summary>
    private string GetAbsoluteResourceMetadataUri()
    {
        var resourceMetadataUri = Options.ResourceMetadataUri;

        string currentPath = resourceMetadataUri?.ToString() ?? string.Empty;

        if (resourceMetadataUri != null && resourceMetadataUri.IsAbsoluteUri)
        {
            return currentPath;
        }

        // For relative URIs, combine with the base URL
        string baseUrl = GetBaseUrl();
        string relativePath = resourceMetadataUri?.OriginalString.TrimStart('/') ?? string.Empty;

        if (!Uri.TryCreate($"{baseUrl.TrimEnd('/')}/{relativePath}", UriKind.Absolute, out var absoluteUri))
        {
            throw new InvalidOperationException($"Could not create absolute URI for resource metadata. Base URL: {baseUrl}, Relative Path: {relativePath}");
        }

        var currentRequestPath = GetCurrentPath();
        return $"{absoluteUri}{currentRequestPath}";
    }

    private async Task<bool> HandleResourceMetadataRequestAsync()
    {
        var resourceMetadata = Options.ResourceMetadata;

        if (Options.Events.OnResourceMetadataRequest is not null)
        {
            var resourceMetadataUri = Options.ResourceMetadataUri ?? throw new InvalidOperationException("ResourceMetadataUri must be configured.");
            var prefix = new PathString(resourceMetadataUri.IsAbsoluteUri ? resourceMetadataUri.AbsolutePath : resourceMetadataUri.OriginalString);

            if (!Request.Path.StartsWithSegments(prefix, out var subPath))
            {
                throw new InvalidOperationException($"Request path '{Request.Path}' does not start with '{prefix}'.");
            }

            var context = new ResourceMetadataRequestContext(Request.HttpContext, Scheme, Options)
            {
                ResourceMetadata = CloneResourceMetadata(resourceMetadata, subPath),
            };

            await Options.Events.OnResourceMetadataRequest(context);

            if (context.Result is not null)
            {
                if (context.Result.Handled)
                {
                    return true;
                }
                else if (context.Result.Skipped)
                {
                    return false;
                }
                else if (context.Result.Failure is not null)
                {
                    throw new AuthenticationFailureException("An error occurred from the OnResourceMetadataRequest event.", context.Result.Failure);
                }
            }

            resourceMetadata = context.ResourceMetadata;
        }

        if (resourceMetadata == null)
        {
            throw new InvalidOperationException(
                "ResourceMetadata has not been configured. Please set McpAuthenticationOptions.ResourceMetadata or ensure context.ResourceMetadata is set inside McpAuthenticationOptions.Events.OnResourceMetadataRequest.");
        }

        await Results.Json(resourceMetadata, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ProtectedResourceMetadata))).ExecuteAsync(Context);
        return true;
    }

    /// <inheritdoc />
    // If no forwarding is configured, this handler doesn't perform authentication
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Get the absolute URI for the resource metadata
        string rawPrmDocumentUri = GetAbsoluteResourceMetadataUri();

        properties ??= new AuthenticationProperties();

        // Store the resource_metadata in properties in case other handlers need it
        properties.Items["resource_metadata"] = rawPrmDocumentUri;

        // Add the WWW-Authenticate header with Bearer scheme and resource metadata
        string headerValue = $"Bearer realm=\"{Scheme.Name}\", resource_metadata=\"{rawPrmDocumentUri}\"";
        Response.Headers.Append("WWW-Authenticate", headerValue);

        return base.HandleChallengeAsync(properties);
    }

    internal static ProtectedResourceMetadata? CloneResourceMetadata(ProtectedResourceMetadata? resourceMetadata, string subPath)
    {
        if (resourceMetadata is null)
        {
            return null;
        }

        if (!Uri.TryCreate(resourceMetadata.Resource, UriKind.Absolute, out var resourceUri))
            throw new InvalidOperationException("ResourceMetadata.Resource must be an absolute URI.");

        return new ProtectedResourceMetadata
        {
            Resource = new Uri(resourceUri, subPath).AbsoluteUri,
            AuthorizationServers = [.. resourceMetadata.AuthorizationServers],
            BearerMethodsSupported = [.. resourceMetadata.BearerMethodsSupported],
            ScopesSupported = [.. resourceMetadata.ScopesSupported],
            JwksUri = resourceMetadata.JwksUri,
            ResourceSigningAlgValuesSupported = resourceMetadata.ResourceSigningAlgValuesSupported is not null ? [.. resourceMetadata.ResourceSigningAlgValuesSupported] : null,
            ResourceName = resourceMetadata.ResourceName,
            ResourceDocumentation = resourceMetadata.ResourceDocumentation,
            ResourcePolicyUri = resourceMetadata.ResourcePolicyUri,
            ResourceTosUri = resourceMetadata.ResourceTosUri,
            TlsClientCertificateBoundAccessTokens = resourceMetadata.TlsClientCertificateBoundAccessTokens,
            AuthorizationDetailsTypesSupported = resourceMetadata.AuthorizationDetailsTypesSupported is not null ? [.. resourceMetadata.AuthorizationDetailsTypesSupported] : null,
            DpopSigningAlgValuesSupported = resourceMetadata.DpopSigningAlgValuesSupported is not null ? [.. resourceMetadata.DpopSigningAlgValuesSupported] : null,
            DpopBoundAccessTokensRequired = resourceMetadata.DpopBoundAccessTokensRequired
        };
    }
}
