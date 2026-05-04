// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// Lifecycle state of a session.
    /// </summary>
    public enum SessionStatus
    {
        Pending = 0,
        Running = 1,
        /// <summary>Reserved: agent paused waiting for human / external input. Not yet emitted.</summary>
        Idle = 2,
        Completed = 3,
        Failed = 4,
        Terminated = 5,
        /// <summary>Caller cancelled the run before it completed. Reserved; not yet emitted.</summary>
        Cancelled = 6,
        /// <summary>Run is paused waiting on a tool/user response. Reserved; not yet emitted.</summary>
        AwaitingInput = 7,
    }
}
