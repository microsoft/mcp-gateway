// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Caching.Memory;
using Microsoft.McpGateway.Management.Authorization;
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
        private static readonly string ToolResourcesCacheKey = $"{typeof(StorageToolDefinitionProvider).FullName}.ToolResources";

        private readonly IToolResourceStore _toolResourceStore;
        private readonly IPermissionProvider _permissionProvider;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<StorageToolDefinitionProvider> _logger;
        private readonly IMemoryCache _cache;
        private readonly SemaphoreSlim _cacheLock = new(1, 1);

        /// <summary>
        /// Initializes a new instance of the <see cref="StorageToolDefinitionProvider"/> class.
        /// </summary>
        public StorageToolDefinitionProvider(
            IToolResourceStore toolResourceStore,
            IPermissionProvider permissionProvider,
            IHttpContextAccessor httpContextAccessor,
            IMemoryCache cache,
            ILogger<StorageToolDefinitionProvider> logger)
        {
            ArgumentNullException.ThrowIfNull(toolResourceStore);
            ArgumentNullException.ThrowIfNull(permissionProvider);
            ArgumentNullException.ThrowIfNull(httpContextAccessor);
            ArgumentNullException.ThrowIfNull(cache);
            ArgumentNullException.ThrowIfNull(logger);

            _toolResourceStore = toolResourceStore;
            _permissionProvider = permissionProvider;
            _httpContextAccessor = httpContextAccessor;
            _cache = cache;
            _logger = logger;

            _logger.LogInformation("Storage tool definition provider initialized");
        }

        public async Task<List<ToolDefinition>> GetToolDefinitionsAsync(CancellationToken cancellationToken = default)
        {
            if (_cache.TryGetValue(ToolResourcesCacheKey, out List<ToolResource>? cachedResources) && cachedResources != null)
            {
                return await FilterToolDefinitionsAsync(cachedResources, cancellationToken).ConfigureAwait(false);
            }

            await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Double-check after acquiring lock
                if (_cache.TryGetValue(ToolResourcesCacheKey, out cachedResources) && cachedResources != null)
                {
                    return await FilterToolDefinitionsAsync(cachedResources, cancellationToken).ConfigureAwait(false);
                }

                // Get all tool resources from the store
                var toolResources = (await _toolResourceStore.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();

                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(TimeSpan.FromMinutes(CacheExpirationMinutes));
                _cache.Set(ToolResourcesCacheKey, toolResources, cacheOptions);

                _logger.LogInformation("Loaded {Count} tool resources from store", toolResources.Count);
                return await FilterToolDefinitionsAsync(toolResources, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load tool definitions from store");
                return [];
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        public async Task<ToolDefinition?> GetToolDefinitionAsync(string toolName, CancellationToken cancellationToken = default)
        {
            try
            {
                var toolResource = await _toolResourceStore.TryGetAsync(toolName, cancellationToken).ConfigureAwait(false);
                if (toolResource == null)
                {
                    return null;
                }

                var principal = _httpContextAccessor.HttpContext?.User;
                if (principal == null)
                {
                    _logger.LogWarning("Missing user context while retrieving tool definition for {ToolName}", toolName);
                    return null;
                }

                if (!await _permissionProvider.CheckAccessAsync(principal, toolResource, Operation.Read).ConfigureAwait(false))
                {
                    _logger.LogWarning("User {UserId} denied read access when retrieving tool definition for {ToolName}", principal.Identity?.Name, toolName);
                    return null;
                }

                return toolResource.ToolDefinition;
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

        private async Task<List<ToolDefinition>> FilterToolDefinitionsAsync(IEnumerable<ToolResource> toolResources, CancellationToken cancellationToken)
        {
            var principal = _httpContextAccessor.HttpContext?.User;
            if (principal == null)
            {
                _logger.LogWarning("Missing user context while filtering tool definitions");
                return [];
            }

            var allowedResources = await _permissionProvider.CheckAccessAsync(principal, toolResources, Operation.Read).ConfigureAwait(false);
            return allowedResources
                .Where(resource => resource.ToolDefinition != null)
                .Select(resource => resource.ToolDefinition!)
                .ToList();
        }
    }
}
