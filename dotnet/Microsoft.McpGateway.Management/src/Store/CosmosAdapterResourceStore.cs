// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Extensions;

namespace Microsoft.McpGateway.Management.Store
{
    public class CosmosAdapterResourceStore : IAdapterResourceStore
    {
        private readonly Container _container;
        private readonly ILogger _logger;
        private readonly CosmosClient _client;
        private readonly string _databaseId;
        private readonly string _containerId;

        public CosmosAdapterResourceStore(CosmosClient client, string databaseId, string containerId, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentException.ThrowIfNullOrEmpty(databaseId);
            ArgumentException.ThrowIfNullOrEmpty(containerId);

            _databaseId = databaseId;
            _containerId = containerId;
            _client = client;
            _container = client.GetContainer(databaseId, containerId);
            _logger = logger;
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            var db = await _client.CreateDatabaseIfNotExistsAsync(_databaseId, cancellationToken: cancellationToken).ConfigureAwait(false);
            await db.Database.CreateContainerIfNotExistsAsync(
                new ContainerProperties
                {
                    Id = _containerId,
                    PartitionKeyPath = "/id"
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<AdapterResource?> TryGetAsync(string name, CancellationToken cancellationToken)
        {
            try
            {
                var response = await _container.ReadItemAsync<AdapterResource>(
                    id: name,
                    partitionKey: new PartitionKey(name),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Cannot find /adapters/{name}, returning NULL", name.Sanitize());
                return null;
            }
        }

        public async Task UpsertAsync(AdapterResource adapter, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream();
            await JsonSerializer.SerializeAsync(stream, adapter, cancellationToken: cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            var response = await _container.UpsertItemStreamAsync(
                stream,
                partitionKey: new PartitionKey(adapter.Id),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        public async Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            try
            {
                await _container.DeleteItemAsync<AdapterResource>(
                    id: name,
                    partitionKey: new PartitionKey(name),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Cannot find /adapters/{name} for deleting, skip the following tasks", name.Sanitize());
            }
        }

        public async Task<IEnumerable<AdapterResource>> ListAsync(CancellationToken cancellationToken)
        {
            var query = _container.GetItemQueryIterator<AdapterResource>();
            var results = new List<AdapterResource>();

            while (query.HasMoreResults)
            {
                var response = await query.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                results.AddRange(response.Resource);
            }

            return results;
        }
    }
}
