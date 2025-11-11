// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace Microsoft.McpGateway.Management.Contracts
{
    public interface IManagedResource
    {
        string Name { get; }

        string CreatedBy { get; }

        IList<string> RequiredRoles { get; }
    }
}
