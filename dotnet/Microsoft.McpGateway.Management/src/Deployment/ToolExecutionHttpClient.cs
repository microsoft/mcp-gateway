// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.McpGateway.Management.Deployment
{
    /// <summary>
    /// Named <see cref="HttpClient"/> used to send tool invocations to tool pods.
    /// </summary>
    public static class ToolExecutionHttpClient
    {
        public const string Name = "ToolExecutionClient";

        /// <summary>
        /// Registers the tool execution client. Tool pods run tenant-supplied images, so their
        /// redirects are never followed: following one would make the gateway request any host
        /// it can reach and return that response to the caller.
        /// </summary>
        public static IHttpClientBuilder AddToolExecutionHttpClient(this IServiceCollection services) =>
            services.AddHttpClient(Name)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
    }
}
