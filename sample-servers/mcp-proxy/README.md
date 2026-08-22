# Proxying Local & Remote MCP Servers

## What’s New

### Proxying Local Stdio MCP Server
- Spin up local MCP servers behind the gateway by specifying a command (`npx`, `uvx`, etc.).  
- Expose them remotely via HTTP/Streamable so cloud agents can connect.  
- Support workload identity for secure authentication with Azure resources.  

With this, you can transform **local-only MCP servers** into **cloud-accessible services** that plug directly into your AI workflows.

### Proxying Remote HTTP MCP Server
- Forward requests from the gateway to an existing MCP server over Streamable HTTP.    


## Instructions

### Preparation

- Make sure the cloud deployment has been done.
- Build the MCP proxy server image in ACR.
  ```sh
  az acr build -r "mgreg$resourceLabel" -f sample-servers/mcp-proxy/Dockerfile sample-servers/mcp-proxy -t "mgreg$resourceLabel.azurecr.io/mcp-proxy:1.0.0"
  ```

- Configure permissions for the workload identity principal (If setting up a local mcp server)
`mg-identity-<identifier>-workload`. 
This identity is created by deployment. The MCP server will use the workload identity for upstream resource access.

### Proxying Local Servers
For starting a local MCP server in stdio and proxying the traffic through gateway to it.
Set server startup command and arguments in environment variables:
  - `MCP_COMMAND`
  - `MCP_ARGS`

Set `useWorkloadIdentity` to be true if need the server to use the workload identity.

  > **Note:** When using a bridged local server, certain system packages may be missing by default. To address this, you can install the required packages within a custom Dockerfile and build your own `mcp-proxy` image.

### Proxying Remote Servers
For proxying another internal mcp server hosted in streamable HTTP. Set the target endpoint in environment variable
  - `MCP_PROXY_URL`
  - `MCP_PROXY_HEADERS_SECRET_URL` as an optional Azure Key Vault secret URL

The referenced secret must contain a JSON object of upstream HTTP headers. Store
credentials only in Key Vault. The gateway stores and returns the secret URL,
but never receives the secret value. Raw `MCP_PROXY_HEADERS` values are rejected.


## Examples

Example payloads to send to `mcp-gateway` using the `POST /adapters` endpoint to launch a mcp server remotely. 

#### Example 1: Bridged [Azure MCP Server](https://github.com/microsoft/mcp/tree/main/servers/Azure.Mcp.Server)
```json
{
  "name": "azure-remote",
  "imageName": "mcp-proxy",
  "imageVersion": "1.0.0",
  "environmentVariables": {
    "MCP_COMMAND": "npx",
    "MCP_ARGS": "-y @azure/mcp@latest server start",
    "AZURE_MCP_INCLUDE_PRODUCTION_CREDENTIALS": "true",
    "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT": "1"
  },
  "description": "Bridged Azure local MCP server"
}
```

#### Example 2: Bridged [Azure AI Foundry MCP Server](https://github.com/azure-ai-foundry/mcp-foundry)
```json
{
  "name": "foundry-remote",
  "imageName": "mcp-proxy",
  "imageVersion": "1.0.0",
  "environmentVariables": {
      "MCP_COMMAND": "uvx",
      "MCP_ARGS": "--prerelease=allow --from git+https://github.com/azure-ai-foundry/mcp-foundry.git run-azure-ai-foundry-mcp"
  },
  "useWorkloadIdentity": true,
  "description": "Bridged Azure AI Foundry Local MCP Server"
}
```

#### Example 3: Bridged [Azure DevOps MCP Server](https://github.com/microsoft/azure-devops-mcp)
```json
{
    "name": "ado-remote",
    "imageName": "mcp-proxy",
    "imageVersion": "1.0.0",
    "environmentVariables": {
      "MCP_COMMAND": "npx",
      "MCP_ARGS": "-y @azure-devops/mcp contoso",
      "ADO_MCP_AZURE_TOKEN_CREDENTIALS": "WorkloadIdentityCredential",
      "AZURE_TOKEN_CREDENTIALS": "WorkloadIdentityCredential"
    },
    "useWorkloadIdentity": true,
    "description": "Bridged ADO MCP Local Server"
}
```

> **Note:** Different MCP servers have different conventions for reading credentials from the environment for setting up `TokenCredential` and connect to upstream resources. You may need to adjust the environment variable names/values per server.<br>
Examples:
Some servers expect a general switch like `AZURE_TOKEN_CREDENTIALS=WorkloadIdentityCredential`
Others use service-specific variables (e.g., `ADO_MCP_AZURE_TOKEN_CREDENTIALS`)

#### Example 4: Proxied Internal MCP Server (Streamable HTTP)
```json
{
    "name": "internal-mcp",
    "imageName": "mcp-proxy",
    "imageVersion": "1.0.0",
    "environmentVariables": {
      "MCP_PROXY_URL": "https://internal-mcp-server/mcp"
    },
    "description": "Proxied Internal MCP Server"
}
```

#### Example 5: Proxied Authenticated MCP Server

Prefer the upstream provider's OAuth flow when available. For a server that
requires a static bearer token, store this JSON in Key Vault:

```json
{"Authorization":"Bearer <token>"}
```

Grant the adapter workload identity permission to read that secret. Then submit
only its URL through the management API:

```json
{
    "name": "authenticated-remote",
    "imageName": "mcp-proxy",
    "imageVersion": "1.0.0",
    "environmentVariables": {
      "MCP_PROXY_URL": "https://mcp.example.com/mcp",
      "MCP_PROXY_HEADERS_SECRET_URL": "https://<vault>.vault.azure.net/secrets/<secret-name>"
    },
    "useWorkloadIdentity": true,
    "description": "Proxied authenticated MCP server"
}
```

## Security Considerations

Before running in production
- Implement appropriate access controls on the gateway level to prevent users from exploiting the workload identity access through it.
- Always only register trusted MCP servers, enable network access policies on the server pods.
- Do not put credentials in adapter `environmentVariables`; management GET and LIST responses include those values.
- Store proxy headers in Key Vault and pass only `MCP_PROXY_HEADERS_SECRET_URL`.
- Use an `https://` upstream whenever proxy headers are configured.
- The proxy rejects redirects, malformed headers, control characters, and non-ASCII header values before connecting upstream.
