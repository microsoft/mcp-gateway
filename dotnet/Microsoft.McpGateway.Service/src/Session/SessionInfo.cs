// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.McpGateway.Service.Session
{
    public class SessionInfo
    {
        /// <summary>
        /// The session's backend target address.
        /// </summary>
        public string Target { get; }

        /// <summary>
        /// The last used time for the session.
        /// </summary>
        public DateTime LastUsed { get; set; }

        [JsonConstructor]
        public SessionInfo(string target, DateTime lastUsed)
        {
            ArgumentException.ThrowIfNullOrEmpty(target);

            Target = target;
            LastUsed = lastUsed;
        }
    }
}
