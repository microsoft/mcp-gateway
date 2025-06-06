// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// This class represents the data for an adapter, including its name, image details, protocol, connection type, environment variables, and description.
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
        /// The protocol used by the adapter. Default is MCP.
        /// </summary>
        [JsonPropertyOrder(6)]
        public ServerProtocol Protocol { get; set; } = ServerProtocol.MCP;

        /// <summary>
        /// The connection type used by the adapter. Default is SSE.
        /// </summary>
        [JsonPropertyOrder(7)]
        public ConnectionType ConnectionType { get; set; } = ConnectionType.StreamableHttp;

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
        [JsonPropertyOrder(8)]
        public string Description { get; set; } = string.Empty;

        public AdapterData(
            string name,
            string imageName,
            string imageVersion,
            Dictionary<string, string>? environmentVariables = null,
            int? replicaCount = 1,
            string description = "",
            ServerProtocol protocol = ServerProtocol.MCP,
            ConnectionType connectionType = ConnectionType.StreamableHttp)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentException.ThrowIfNullOrEmpty(imageName);
            ArgumentException.ThrowIfNullOrEmpty(imageVersion);

            Name = name;
            ImageName = imageName;
            ImageVersion = imageVersion;
            Protocol = protocol;
            ConnectionType = connectionType;
            EnvironmentVariables = environmentVariables ?? [];
            ReplicaCount = replicaCount ?? 1;
            Description = description;
        }

        public AdapterData() { }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ConnectionType
    {
        SSE,
        StreamableHttp,
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ServerProtocol
    {
        MCP
    }
}
