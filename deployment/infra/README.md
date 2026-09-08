# MCP Gateway Azure Deployment

This directory contains infrastructure-as-code templates and scripts for deploying the MCP Gateway to Azure.

## Deployment Options

There are two ways to deploy the MCP Gateway infrastructure:

### Option 1: PowerShell Script (Recommended)

Use the PowerShell deployment script for better control and separation of concerns. This approach:
- Deploys Azure infrastructure using Bicep
- Separately configures Kubernetes resources
- Provides better error handling and progress feedback

**Prerequisites:**
- Azure CLI installed and authenticated (`az login`)
- PowerShell 7.4 or higher
- Appropriate Azure permissions

**Basic Usage:**

```powershell
.\deployment\Deploy-McpGateway.ps1 -SubscriptionId "<subscription-id>" -TenantId "<tenant-id>" -ResourceGroupName "rg-mcpgateway-dev" -ResourceLabel "mcpdev" -ClientId "<your-entra-client-id>" -Stage Infrastructure
```

Run examples from the repository root. Use a lowercase alphanumeric `ResourceLabel`, even when the resource group name contains hyphens. Configure HTTPS before sending bearer tokens; without certificate parameters the template exposes an HTTP listener intended only for infrastructure bring-up.

**Advanced Usage:**

```powershell
# Deploy to a specific region with a custom resource label
.\deployment\Deploy-McpGateway.ps1 `
    -ResourceGroupName "rg-mcpgateway-prod" `
    -ClientId "<your-entra-client-id>" `
    -ResourceLabel "mcpprod" `
    -Location "westus2"

# Deploy with private endpoints enabled
.\deployment\Deploy-McpGateway.ps1 `
    -ResourceGroupName "rg-mcpgateway-secure" `
    -ClientId "<your-entra-client-id>" `
  -ResourceLabel "mcpsecure" `
    -EnablePrivateEndpoints
```

**Parameters:**

| Parameter | Required | Description |
|-----------|----------|-------------|
| `ResourceGroupName` | Yes | Name of the Azure resource group (created if doesn't exist) |
| `ClientId` | Yes | Entra ID client ID for authentication |
| `ResourceLabel` | No | Alphanumeric suffix for resource naming (3-30 chars). Defaults to resource group name |
| `Location` | No | Azure region for deployment. Default: `westus3` |
| `EnablePrivateEndpoints` | No | Create a Cosmos DB private endpoint and DNS link, and disable its public access; does not configure private ACR access |
| `SubscriptionId`, `TenantId` | No | Select the subscription and verify its tenant; specify both for repeatable deployments |
| `Stage` | No | `Infrastructure`, `Kubernetes`, or `All` (default) |
| `DeploymentName` | No | ARM deployment name reused to read outputs; default `mcpgateway` |
| `GatewayImage`, `ToolGatewayImage` | No | Exact first-party image references; supply your tested tags or digests rather than relying on default `latest` images |
| `NodeCount`, `NodeVmSize` | No | Default: two `Standard_D4ds_v5` nodes; size a separate test deployment explicitly |
| `AcrSku` | No | `Basic`, `Standard` (default), or `Premium` |
| `CosmosServerless` | No | Enable serverless on a new test account; not an in-place conversion |
| `SecureParametersFile` | No | Path to an ARM parameter file with secure TLS certificate values; keep outside source control |
| `KubernetesTemplatePath` | No | Defaults to the checked-out `deployment/k8s/cloud-deployment-template.yml` |

### HTTPS and Image Deployment

Provide `tlsCertificateData` (base64-encoded PFX) and `tlsCertificatePassword` as secure parameters through `SecureParametersFile`. Use a certificate valid for the public FQDN. Protect the parameter file and never commit or print its contents.

Use the same subscription, tenant, resource label, client ID, and deployment name for both stages:

1. Compile the Bicep template, run ARM validation, and inspect `what-if` using your selected sizing and secure parameters.
2. Run `-Stage Infrastructure` to provision AKS, ACR, storage, and ingress.
3. Build and publish the gateway, Tools, and adapter images to ACR. Record the exact image references.
4. Ensure the deployment operator has Kubernetes data-plane permissions to create the namespace, Roles, RoleBindings, and secrets. Azure resource Owner alone does not grant these permissions on Azure-RBAC-enabled AKS; use an appropriately scoped AKS RBAC role.
5. Run `-Stage Kubernetes` with `-GatewayImage` and `-ToolGatewayImage`. The script reads the local manifest and preserves an existing gateway secret or generates one when absent.
6. Verify the HTTPS `publicOrigin`, pod readiness, image digests, and authenticated MCP requests. Both clients and adapters must support MCP `2026-07-28`.

For a new short-lived test deployment only, `-NodeCount 1 -NodeVmSize Standard_D4as_v5 -AcrSku Basic -CosmosServerless` reduces the default footprint, subject to current regional capacity and AKS requirements. Application Gateway still has a base charge. See [end-to-end testing](../e2e/README.md) for validation commands.

### Option 2: Direct Bicep Deployment

Deploy infrastructure directly using Bicep, then use the PowerShell Kubernetes
stage with the same deployment name and exact image references. The examples
disable the embedded script so image publishing can happen before pod creation.

The optional embedded-script path downloads its manifest from the published
branch. It requires explicit compatible image references and a secure
`gatewaySecret` value and is not suitable for validating unpublished checkout
changes.

```bash
# Create resource group
az group create --name rg-mcpgateway-dev --location eastus

# Deploy using Bicep
az deployment group create \
  --name mcpgateway-deployment \
  --resource-group rg-mcpgateway-dev \
  --template-file deployment/infra/azure-deployment.bicep \
  --parameters clientId=<your-entra-client-id> resourceLabel=mcpdev enableKubernetesDeploymentScript=false
```

**With additional parameters:**

```bash
az deployment group create \
  --name mcpgateway-deployment \
  --resource-group rg-mcpgateway-dev \
  --template-file deployment/infra/azure-deployment.bicep \
  --parameters \
    clientId=<your-entra-client-id> \
    resourceLabel=mcpdev \
    location=westus2 \
    enablePrivateEndpoints=true \
    enableKubernetesDeploymentScript=false
```

**Disable embedded Kubernetes deployment script:**

```bash
az deployment group create \
  --name mcpgateway-deployment \
  --resource-group rg-mcpgateway-dev \
  --template-file deployment/infra/azure-deployment.bicep \
  --parameters \
    clientId=<your-entra-client-id> \
    enableKubernetesDeploymentScript=false
```

## Deployed Resources

The deployment creates the following Azure resources:

### Core Infrastructure
- **Azure Kubernetes Service (AKS)**: Managed Kubernetes cluster
  - 2-node cluster with D4ds_v5 VMs
  - Azure RBAC enabled
  - OIDC issuer and Workload Identity enabled
  
- **Azure Container Registry (ACR)**: Container image storage
  - Standard SKU
  - Integrated with AKS for image pull

- **Azure Cosmos DB**: Document database for gateway state
  - Session consistency level
  - Three containers: AdapterContainer, CacheContainer, ToolContainer
  - Optional private endpoint support

### Networking
- **Virtual Network (VNet)**: Network isolation
  - 10.0.0.0/16 address space
  - AKS subnet (10.0.1.0/24)
  - Application Gateway subnet (10.0.2.0/24)
  - Private endpoint subnet (10.0.3.0/24)

- **Application Gateway**: Layer 7 load balancer
  - Standard_v2 SKU
  - HTTPS frontend on port 443 when TLS parameters are supplied; otherwise HTTP on port 80
  - Response buffering disabled and a 600-second backend timeout for streaming
  - Health probe for backend monitoring

- **Public IP**: Static public IP with DNS label

### Identity & Access
- **Managed Identities**:
  - Gateway service identity (with Cosmos DB data contributor role)
  - Admin identity (for AKS operations)
  - Workload identity (for pod-level authentication)
  
- **Federated Credentials**: For Kubernetes service account integration

### Monitoring
- **Application Insights**: Application monitoring and telemetry

## Networking Options

### Public Access (Default)
The template requests public network access by default. Organization policy may override it; check effective settings. Use HTTPS and Entra authentication for gateway traffic. ACR anonymous pull and admin access are disabled; Cosmos local key authentication is disabled.

### Private Endpoints
Enable with `-EnablePrivateEndpoints` flag:
- Cosmos DB is reachable through a private endpoint in the deployment VNet, with public access disabled
- Its private DNS zone and VNet link are configured automatically
- ACR remains publicly reachable with Entra authentication; private ACR networking is not implemented by this option

## Post-Deployment

After successful deployment:

1. **Access the Gateway**: Use the FQDN from the deployment output
   ```
  https://<public-ip-dns-label>.<region>.cloudapp.azure.com
   ```

2. **Verify Kubernetes Pods**:
   ```bash
   kubectl get pods -n adapter
   ```

3. **Check Gateway Logs**:
   ```bash
  kubectl logs -n adapter -l app=mcpgateway
   ```

## Troubleshooting

### PowerShell Script Issues

**Prerequisites not met:**
- Ensure Azure CLI is installed: `az --version`
- Login to Azure: `az login`

**Deployment failures:**
- Check Azure CLI output for specific errors
- Verify you have appropriate permissions in the subscription
- Ensure the resource label is unique and meets naming requirements

**Kubernetes deployment issues:**
- Verify AKS cluster is running: `az aks show -g <rg-name> -n <aks-name>`
- Review pod status: `az aks command invoke -g <rg-name> -n <aks-name> --command "kubectl get pods -A"`

### Bicep Deployment Issues

**Template validation errors:**
```bash
az deployment group validate \
  --resource-group <rg-name> \
  --template-file deployment/infra/azure-deployment.bicep \
  --parameters clientId=<your-client-id>
```

**Deployment script failures:**
- Check deployment script logs in Azure Portal
- Verify managed identity has appropriate permissions
- Review AKS cluster accessibility

## Cleanup

To remove all deployed resources:

```powershell
# Delete the entire resource group
az group delete --name rg-mcpgateway-dev --yes --no-wait
```

## Migration from Embedded Script to PowerShell

If you previously deployed using the embedded Bicep deployment script:

1. The Bicep template now supports both methods via the `enableKubernetesDeploymentScript` parameter
2. To update an existing deployment without the embedded script:
   ```powershell
  .\deployment\Deploy-McpGateway.ps1 -ResourceGroupName <existing-rg> -ClientId <client-id> -ResourceLabel <existing-label>
   ```
3. The PowerShell script will update the infrastructure and reconfigure Kubernetes resources

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      Internet                               │
└────────────────────────┬────────────────────────────────────┘
                         │
                    ┌────▼─────┐
                    │ Public IP│
                    └────┬─────┘
                         │
                ┌────────▼──────────┐
                │ Application       │
                │ Gateway           │
                └────────┬──────────┘
                         │
        ┌────────────────┼────────────────┐
        │                                 │
┌───────▼────────┐              ┌────────▼────────┐
│                │              │                 │
│  AKS Cluster   │──────────────│  ACR            │
│                │              │  (Images)       │
└───────┬────────┘              └─────────────────┘
        │
        │ Workload Identity
        │
┌───────▼────────┐              ┌─────────────────┐
│                │              │                 │
│  Cosmos DB     │              │  App Insights   │
│  (State)       │              │  (Monitoring)   │
└────────────────┘              └─────────────────┘
```

## Security Considerations

- **Authentication**: Uses Entra ID (Azure AD) for authentication
- **Authorization**: Azure RBAC for AKS, Cosmos DB RBAC for data access
- **Network Isolation**: Optional private endpoints for enhanced security
- **Identity**: Workload Identity for Azure data access. A separate gateway secret authenticates first-party identity forwarding; user-deployed adapters must never receive it.
- **Secrets**: Managed identities eliminate need for storing credentials

## Additional Resources

- [Azure Kubernetes Service Documentation](https://docs.microsoft.com/azure/aks/)
- [Azure Container Registry Documentation](https://docs.microsoft.com/azure/container-registry/)
- [Azure Cosmos DB Documentation](https://docs.microsoft.com/azure/cosmos-db/)
- [Bicep Documentation](https://docs.microsoft.com/azure/azure-resource-manager/bicep/)
