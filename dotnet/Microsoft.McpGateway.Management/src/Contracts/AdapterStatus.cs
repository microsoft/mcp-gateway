// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// Contract for the deployed adapter status.
    /// </summary>
    public class AdapterStatus
    {
        public int? ReadyReplicas { get; set; }

        public int? UpdatedReplicas { get; set; }

        public int? AvailableReplicas { get; set; }

        public string? Image { get; set; }

        public string? ReplicaStatus { get; set; }
    }
}
