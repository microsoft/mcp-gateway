﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.McpGateway.Management.Contracts
{
    public class AdapterResource : AdapterData
    {
        [JsonPropertyOrder(-1)]
        public required string Id { get; set; }

        /// <summary>
        /// The ID of the user who created the adapter.
        /// </summary>
        [JsonPropertyOrder(9)]
        public required string CreatedBy { get; set; }

        /// <summary>
        /// The date and time when the adapter was created.
        /// </summary>
        [JsonPropertyOrder(10)]
        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>
        /// The date and time when the adapter was created.
        /// </summary>
        [JsonPropertyOrder(11)]
        public DateTimeOffset LastUpdatedAt { get; set; }

        public AdapterResource(AdapterData adapterData, string createdBy, DateTimeOffset createdAt, DateTimeOffset lastUpdatedAt)
            : base(adapterData.Name, adapterData.ImageName, adapterData.ImageVersion, adapterData.EnvironmentVariables, adapterData.ReplicaCount, adapterData.Description, adapterData.Protocol, adapterData.ConnectionType)
        {
            CreatedBy = createdBy;
            CreatedAt = createdAt;
            LastUpdatedAt = lastUpdatedAt;
        }

        public static AdapterResource Create(AdapterData data, string createdBy, DateTimeOffset createdAt) =>
            new()
            {
                Id = data.Name,
                Name = data.Name,
                ImageName = data.ImageName,
                ImageVersion = data.ImageVersion,
                EnvironmentVariables = data.EnvironmentVariables,
                ReplicaCount = data.ReplicaCount,
                Description = data.Description,
                Protocol = data.Protocol,
                ConnectionType = data.ConnectionType,
                CreatedBy = createdBy,
                CreatedAt = createdAt,
                LastUpdatedAt = DateTime.UtcNow
            };

        public AdapterResource() { }
    }
}
