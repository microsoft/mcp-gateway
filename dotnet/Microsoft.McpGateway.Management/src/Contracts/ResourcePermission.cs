// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// Represents the authorization metadata for a managed resource.
    /// </summary>
    public class ResourcePermission : IManagedResource
    {
        [JsonPropertyOrder(-1)]
        [JsonPropertyName("id")]
        public required string ResourceId { get; set; }

        public required string Owner { get; set; }

        [JsonIgnore]
        public string Name => ResourceId;

        [JsonIgnore]
        public string CreatedBy => Owner;

        public IList<string> RequiredRoles { get; set; } = [];
    }
}
