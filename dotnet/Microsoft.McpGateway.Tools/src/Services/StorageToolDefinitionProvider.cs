// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Tools.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Microsoft.McpGateway.Tools.Services
{
    /// <summary>
    /// Implementation that loads tool definitions from IToolResourceStore.
    /// </summary>
    public class StorageToolDefinitionProvider : IToolDefinitionProvider
    {
        private const int CacheExpirationMinutes = 5;

        private readonly IToolResourceStore _toolResourceStore;
        private readonly ILogger<StorageToolDefinitionProvider> _logger;
        private List<ToolDefinition>? _cachedTools;
        private DateTime _lastLoadTime;

        /// <summary>
        /// Initializes a new instance of the <see cref="StorageToolDefinitionProvider"/> class.
        /// </summary>
        public StorageToolDefinitionProvider(
            IToolResourceStore toolResourceStore,
            ILogger<StorageToolDefinitionProvider> logger)
        {
            ArgumentNullException.ThrowIfNull(toolResourceStore);
            ArgumentNullException.ThrowIfNull(logger);

            _toolResourceStore = toolResourceStore;
            _logger = logger;

            _logger.LogInformation("Storage tool definition provider initialized");
        }

        public async Task<List<ToolDefinition>> GetToolDefinitionsAsync(CancellationToken cancellationToken = default)
        {
            var cacheExpiration = TimeSpan.FromMinutes(CacheExpirationMinutes);

            // Simple caching mechanism
            if (_cachedTools != null && DateTime.UtcNow - _lastLoadTime < cacheExpiration)
            {
                return _cachedTools;
            }

            try
            {
                // Get all tool resources from the store
                var toolResources = await _toolResourceStore.ListAsync(cancellationToken).ConfigureAwait(false);

                // Extract tool definitions from resources
                var toolDefinitions = toolResources
                    .Select(tr => tr.ToolDefinition)
                    .ToList();

                _cachedTools = toolDefinitions;
                _lastLoadTime = DateTime.UtcNow;

                _logger.LogInformation("Loaded {Count} tool definitions from store", _cachedTools.Count);
                return _cachedTools;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load tool definitions from store");
                return [];
            }
        }

        public async Task<ToolDefinition?> GetToolDefinitionAsync(string toolName, CancellationToken cancellationToken = default)
        {
            try
            {
                // Try to get the specific tool resource directly
                var toolResource = await _toolResourceStore.TryGetAsync(toolName, cancellationToken).ConfigureAwait(false);
                return toolResource?.ToolDefinition;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get tool definition for {ToolName}", toolName);
                return null;
            }
        }

        public async ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Listing available MCP tools from storage");

            // Load tool definitions from store
            var toolDefinitions = await GetToolDefinitionsAsync(cancellationToken).ConfigureAwait(false);

            // Convert to MCP Protocol Tools
            var tools = toolDefinitions.Select(td => td.Tool).ToList();

            _logger.LogInformation("Returning {Count} tools from storage", tools.Count);

            return new ListToolsResult
            {
                Tools = tools,
                NextCursor = null
            };
        }
    }
}
