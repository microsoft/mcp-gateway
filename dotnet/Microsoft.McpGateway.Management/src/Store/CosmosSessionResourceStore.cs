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
    /// <summary>
    /// Cosmos DB implementation of the session resource store.
    /// </summary>
    public class CosmosSessionResourceStore : ISessionResourceStore
    {
        private readonly Container _container;
        private readonly ILogger _logger;
        private readonly CosmosClient _client;
        private readonly string _databaseId;
        private readonly string _containerId;

        public CosmosSessionResourceStore(CosmosClient client, string databaseId, string containerId, ILogger logger)
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

        public async Task<SessionResource?> TryGetAsync(string id, CancellationToken cancellationToken)
        {
            try
            {
                var response = await _container.ReadItemAsync<SessionResource>(
                    id: id,
                    partitionKey: new PartitionKey(id),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Cannot find /sessions/{id}, returning NULL", id.Sanitize());
                return null;
            }
        }

        public async Task UpsertAsync(SessionResource session, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream();
            await JsonSerializer.SerializeAsync(stream, session, cancellationToken: cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            var response = await _container.UpsertItemStreamAsync(
                stream,
                partitionKey: new PartitionKey(session.Id),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        public async Task DeleteAsync(string id, CancellationToken cancellationToken)
        {
            try
            {
                await _container.DeleteItemAsync<SessionResource>(
                    id: id,
                    partitionKey: new PartitionKey(id),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Cannot find /sessions/{id} for deleting, skip the following tasks", id.Sanitize());
            }
        }

        public async Task<IEnumerable<SessionResource>> ListAsync(CancellationToken cancellationToken)
        {
            var query = _container.GetItemQueryIterator<SessionResource>();
            var results = new List<SessionResource>();

            while (query.HasMoreResults)
            {
                var response = await query.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                results.AddRange(response.Resource);
            }

            return results;
        }
    }
}
