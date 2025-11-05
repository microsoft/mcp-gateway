// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// This class represents the data for an adapter, including its name, image details, environment variables, and description.
    /// </summary>
    public class AdapterData
    {
        /// <summary>
        /// The name of the adapter. It must contain only lowercase letters, numbers, and dashes.
        /// </summary>
        [JsonPropertyOrder(1)]
        [RegularExpression("^[a-z0-9-]+$", ErrorMessage = "Name must contain only lowercase letters, numbers and dashes.")]
        public required string Name { get; set; }

        /// <summary>
        /// The name of the image associated with the adapter.
        /// </summary>
        [JsonPropertyOrder(2)]
        public required string ImageName { get; set; }

        /// <summary>
        /// The version of the image associated with the adapter.
        /// </summary>
        [JsonPropertyOrder(3)]
        public required string ImageVersion { get; set; }

        /// <summary>
        /// Environment key variables in M3 service for the adapter.
        /// </summary>
        [JsonPropertyOrder(4)]
        public Dictionary<string, string> EnvironmentVariables { get; set; } = [];

        /// <summary>
        /// Replica count for the adapter.
        /// </summary>
        [JsonPropertyOrder(5)]
        public int ReplicaCount { get; set; } = 1;

        /// <summary>
        /// A description of the adapter.
        /// </summary>
        [JsonPropertyOrder(6)]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Indicates whether to use workload identity for the deployed adapter instance. Default is false.
        /// </summary>
        [JsonPropertyOrder(7)]
        public bool UseWorkloadIdentity { get; set; } = false;

        public AdapterData(
            string name,
            string imageName,
            string imageVersion,
            Dictionary<string, string>? environmentVariables = null,
            int? replicaCount = 1,
            string description = "",
            bool useWorkloadIdentity = false)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentException.ThrowIfNullOrEmpty(imageName);
            ArgumentException.ThrowIfNullOrEmpty(imageVersion);

            Name = name;
            ImageName = imageName;
            ImageVersion = imageVersion;
            EnvironmentVariables = environmentVariables ?? [];
            ReplicaCount = replicaCount ?? 1;
            Description = description;
            UseWorkloadIdentity = useWorkloadIdentity;
        }

        public AdapterData() { }
    }
}
