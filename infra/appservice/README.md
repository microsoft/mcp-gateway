# MCP Gateway on Azure App Service

This deployment option runs MCP Gateway on Azure App Service using the new **SITECONTAINERS** feature (multi-container apps), which allows dynamic MCP server deployment without Kubernetes.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Azure App Service                        │
│                   (linux_fx_version=sitecontainers)         │
│                                                             │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐      │
│  │   Gateway    │  │  MCP Server  │  │  MCP Server  │ ...  │
│  │   (main)     │  │  (sidecar)   │  │  (sidecar)   │      │
│  │   :8000      │  │   :8001      │  │   :8002      │      │
│  └──────────────┘  └──────────────┘  └──────────────┘      │
│         │                │                │                 │
│         └────────────────┴────────────────┘                 │
│                    localhost                                │
└─────────────────────────────────────────────────────────────┘
                         │
                         ▼
                    ┌─────────┐
                    │ CosmosDB │  (session storage, adapter registry)
                    └─────────┘
```

## How It Works

1. **Gateway Container**: Runs as the main container, listening on port 8000
2. **MCP Server Sidecars**: Dynamically deployed as sitecontainer resources via ARM API
3. **Networking**: All containers communicate via `localhost` (shared network namespace)
4. **Session Affinity**: Uses Cosmos DB (or Redis) for distributed session storage

### Key Differences from Kubernetes

| Aspect | Kubernetes | App Service |
|--------|------------|-------------|
| Deployment API | K8s API (StatefulSet) | ARM API (sitecontainers) |
| Pod DNS | `pod.service.namespace.svc.cluster.local` | `localhost:{port}` |
| Discovery | K8s Watch API | ARM API query |
| Max replicas | Unlimited | 9 sidecars |
| Scaling | HPA | N/A (single instance) |

## Deployment

### Prerequisites

- Azure subscription
- Azure Container Registry with gateway image
- Cosmos DB account
- Azure AD app registration

### Deploy Infrastructure

```bash
# Create resource group
az group create -n mcp-gateway-rg -l eastus

# Deploy Bicep template
az deployment group create \
  -g mcp-gateway-rg \
  -f infra/appservice/main.bicep \
  -p baseName=mcpgw \
     containerRegistryEndpoint=myregistry.azurecr.io \
     clientId=<your-aad-client-id> \
     cosmosAccountEndpoint=https://mycosmosdb.documents.azure.com:443/

# Grant AcrPull role to managed identity
IDENTITY_PRINCIPAL=$(az deployment group show -g mcp-gateway-rg -n main --query properties.outputs.managedIdentityPrincipalId.value -o tsv)
ACR_ID=$(az acr show -n myregistry --query id -o tsv)
az role assignment create --assignee $IDENTITY_PRINCIPAL --role AcrPull --scope $ACR_ID
```

### Build and Push Gateway Image

```bash
# Build the gateway
cd dotnet
dotnet publish Microsoft.McpGateway.Service/src -c Release -o publish

# Create Dockerfile
cat > publish/Dockerfile <<EOF
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY . .
EXPOSE 8000
ENTRYPOINT ["dotnet", "Microsoft.McpGateway.Service.dll"]
EOF

# Build and push
docker build -t myregistry.azurecr.io/mcp-gateway:latest publish/
az acr login -n myregistry
docker push myregistry.azurecr.io/mcp-gateway:latest
```

## Usage

### Create an Adapter

```bash
curl -X POST https://mcpgw-gateway.azurewebsites.net/adapters \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "my-mcp-server",
    "imageName": "my-mcp-server",
    "imageVersion": "v1.0.0",
    "description": "My custom MCP server"
  }'
```

This creates a new sitecontainer via ARM API, which runs alongside the gateway.

### Connect to an Adapter

```
GET /adapters/my-mcp-server/mcp
Headers:
  Authorization: Bearer <token>
  mcp-session-id: <optional-session-id>
```

The gateway routes to `localhost:800X` where X is the allocated port for that adapter.

## Limitations

- **Max 9 MCP servers**: App Service allows 1 main + 9 sidecar containers
- **No horizontal scaling**: Unlike K8s StatefulSets, each adapter has only 1 instance
- **ARM API latency**: Container creation/deletion is slower than K8s (~30-60s)
- **Shared resources**: All containers share the App Service plan resources

## Configuration

### App Settings

| Setting | Description |
|---------|-------------|
| `AzureAd__TenantId` | Azure AD tenant ID |
| `AzureAd__ClientId` | Azure AD client ID |
| `CosmosSettings__AccountEndpoint` | Cosmos DB endpoint |
| `CosmosSettings__DatabaseName` | Cosmos DB database name |
| `ContainerRegistrySettings__Endpoint` | ACR endpoint |
| `PublicOrigin` | Public URL for MCP auth |
| `AZURE_CLIENT_ID` | Managed identity client ID |

### Environment Detection

The gateway automatically detects App Service mode via `WEBSITE_SITE_NAME` environment variable. When running in App Service:

- Uses `AppServiceDeploymentManager` instead of `KubernetesAdapterDeploymentManager`
- Uses `AppServiceNodeInfoProvider` instead of `AdapterKubernetesNodeInfoProvider`
- Routes to `localhost:{port}` instead of Kubernetes pod DNS

## Troubleshooting

### View Container Logs

```bash
# Stream all logs
az webapp log tail -g mcp-gateway-rg -n mcpgw-gateway

# View specific container logs via Kudu
https://mcpgw-gateway.scm.azurewebsites.net/api/logs/docker
```

### Check Sitecontainers

```bash
# List all sitecontainers
az rest --method GET \
  --url "https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Web/sites/{app}/sitecontainers?api-version=2024-04-01"
```

### Common Issues

1. **Container not starting**: Check ACR pull permissions and managed identity role assignments
2. **Session routing failing**: Verify Cosmos DB connectivity and cache configuration
3. **ARM API errors**: Ensure managed identity has "Website Contributor" role on the App Service
