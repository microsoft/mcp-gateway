// MCP Gateway - App Service Deployment
// This deploys MCP Gateway to Azure App Service using SITECONTAINERS (multi-container apps)

@description('Location for all resources')
param location string = resourceGroup().location

@description('Base name for all resources')
param baseName string

@description('Container registry endpoint (e.g., myregistry.azurecr.io)')
param containerRegistryEndpoint string

@description('Gateway container image name')
param gatewayImageName string = 'mcp-gateway'

@description('Gateway container image tag')
param gatewayImageTag string = 'latest'

@description('Azure AD Tenant ID for authentication')
param tenantId string = subscription().tenantId

@description('Azure AD Client ID for the gateway app')
param clientId string

@description('Cosmos DB account endpoint')
param cosmosAccountEndpoint string

@description('Cosmos DB database name')
param cosmosDatabaseName string = 'mcpgateway'

@description('Redis cache connection string (optional, uses Cosmos Cache if empty)')
param redisConnectionString string = ''

// App Service Plan (Linux Premium V3 for containers)
resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${baseName}-plan'
  location: location
  kind: 'linux'
  sku: {
    name: 'P1v3'
    tier: 'PremiumV3'
  }
  properties: {
    reserved: true // Required for Linux
  }
}

// User-assigned managed identity for the gateway
resource managedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${baseName}-identity'
  location: location
}

// App Service (Web App for Containers with SITECONTAINERS)
resource webApp 'Microsoft.Web/sites@2024-04-01' = {
  name: '${baseName}-gateway'
  location: location
  kind: 'app,linux,container'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentity.id}': {}
    }
  }
  properties: {
    serverFarmId: appServicePlan.id
    siteConfig: {
      linuxFxVersion: 'sitecontainers'  // Enable multi-container mode
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        {
          name: 'DOCKER_REGISTRY_SERVER_URL'
          value: 'https://${containerRegistryEndpoint}'
        }
        {
          name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE'
          value: 'false'
        }
        {
          name: 'WEBSITES_PORT'
          value: '8000'
        }
        // Azure AD settings
        {
          name: 'AzureAd__TenantId'
          value: tenantId
        }
        {
          name: 'AzureAd__ClientId'
          value: clientId
        }
        {
          name: 'AzureAd__Instance'
          value: 'https://login.microsoftonline.com/'
        }
        // Cosmos DB settings
        {
          name: 'CosmosSettings__AccountEndpoint'
          value: cosmosAccountEndpoint
        }
        {
          name: 'CosmosSettings__DatabaseName'
          value: cosmosDatabaseName
        }
        // Container registry
        {
          name: 'ContainerRegistrySettings__Endpoint'
          value: containerRegistryEndpoint
        }
        // Public origin for MCP auth
        {
          name: 'PublicOrigin'
          value: 'https://${baseName}-gateway.azurewebsites.net'
        }
        // Managed identity client ID (for DefaultAzureCredential)
        {
          name: 'AZURE_CLIENT_ID'
          value: managedIdentity.properties.clientId
        }
      ]
    }
    httpsOnly: true
  }
}

// Main container (gateway)
resource gatewayContainer 'Microsoft.Web/sites/sitecontainers@2024-04-01' = {
  parent: webApp
  name: 'gateway'
  properties: {
    image: '${containerRegistryEndpoint}/${gatewayImageName}:${gatewayImageTag}'
    targetPort: '8000'
    isMain: true
    authType: 'UserAssigned'
    userManagedIdentityClientId: managedIdentity.properties.clientId
    environmentVariables: []  // Main container uses app settings
  }
}

// Role assignment: Website Contributor (to manage sitecontainers)
// The gateway needs to create/delete sitecontainers dynamically
resource websiteContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(webApp.id, managedIdentity.id, 'de139f84-1756-47ae-9be6-808fbbe84772')
  scope: webApp
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772') // Website Contributor
    principalId: managedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Role assignment: AcrPull (to pull container images)
// Note: This needs to be assigned at the ACR level, not shown here
// Use: az role assignment create --assignee <principalId> --role AcrPull --scope <acrResourceId>

output webAppName string = webApp.name
output webAppHostname string = webApp.properties.defaultHostName
output managedIdentityClientId string = managedIdentity.properties.clientId
output managedIdentityPrincipalId string = managedIdentity.properties.principalId
