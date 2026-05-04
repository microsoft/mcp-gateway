// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.McpGateway.Management.Foundry
{
    /// <summary>
    /// Configuration for the Foundry / Azure OpenAI chat client.
    /// Bound from the "FoundrySettings" section.
    /// </summary>
    public class FoundrySettings
    {
        /// <summary>
        /// OpenAI-compatible endpoint of the AIServices resource.
        /// </summary>
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>
        /// Default deployment name to call when an agent does not specify one
        /// recognized by Foundry. Example: "gpt-5-chat".
        /// </summary>
        public string DeploymentName { get; set; } = string.Empty;
    }
}
