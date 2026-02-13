// MCP Gateway - Complete App Service Deployment
// Creates all resources: App Service, ACR, Cosmos DB, Managed Identity

@description('Location for all resources')
param location string = resourceGroup().location

@description('Base name for all resources (lowercase, no special chars)')
param baseName string

@description('Gateway container image tag')
param gatewayImageTag string = 'latest'

// Generate unique suffix for globally unique names
var uniqueSuffix = uniqueString(resourceGroup().id)
var acrName = '${replace(baseName, '-', '')}${uniqueSuffix}'
var cosmosName = '${baseName}-cosmos-${uniqueSuffix}'

// Azure Container Registry
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

// Cosmos DB Account
resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: cosmosName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
  }
}

// Cosmos DB Database
resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmosAccount
  name: 'mcpgateway'
  properties: {
    resource: {
      id: 'mcpgateway'
    }
  }
}

// Cosmos DB Containers
resource adapterContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: 'AdapterContainer'
  properties: {
    resource: {
      id: 'AdapterContainer'
      partitionKey: {
        paths: ['/id']
        kind: 'Hash'
      }
    }
  }
}

resource toolContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: 'ToolContainer'
  properties: {
    resource: {
      id: 'ToolContainer'
      partitionKey: {
        paths: ['/id']
        kind: 'Hash'
      }
    }
  }
}

resource cacheContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: 'CacheContainer'
  properties: {
    resource: {
      id: 'CacheContainer'
      partitionKey: {
        paths: ['/id']
        kind: 'Hash'
      }
      defaultTtl: 86400 // 24 hours
    }
  }
}

// User-assigned managed identity
resource managedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${baseName}-identity'
  location: location
}

// App Service Plan (Linux Premium V3)
resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${baseName}-plan'
  location: location
  kind: 'linux'
  sku: {
    name: 'P0v3'
    tier: 'PremiumV3'
  }
  properties: {
    reserved: true
  }
}

// App Service (Web App with SITECONTAINERS)
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
      linuxFxVersion: 'sitecontainers'
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        {
          name: 'DOCKER_REGISTRY_SERVER_URL'
          value: 'https://${acr.properties.loginServer}'
        }
        {
          name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE'
          value: 'false'
        }
        {
          name: 'WEBSITES_PORT'
          value: '8000'
        }
        // Development mode (no Azure AD auth)
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Development'
        }
        // Redis connection for dev mode (using in-memory for simplicity)
        {
          name: 'Redis__ConnectionString'
          value: 'localhost:6379'
        }
        // Cosmos DB settings (for production, uncomment these and set ASPNETCORE_ENVIRONMENT to Production)
        {
          name: 'CosmosSettings__AccountEndpoint'
          value: cosmosAccount.properties.documentEndpoint
        }
        {
          name: 'CosmosSettings__DatabaseName'
          value: 'mcpgateway'
        }
        // Container registry
        {
          name: 'ContainerRegistrySettings__Endpoint'
          value: acr.properties.loginServer
        }
        // Public origin
        {
          name: 'PublicOrigin'
          value: 'https://${baseName}-gateway.azurewebsites.net'
        }
        // Managed identity client ID
        {
          name: 'AZURE_CLIENT_ID'
          value: managedIdentity.properties.clientId
        }
      ]
    }
    httpsOnly: true
  }
}

// Main gateway container (placeholder - will be updated after image push)
resource gatewayContainer 'Microsoft.Web/sites/sitecontainers@2024-04-01' = {
  parent: webApp
  name: 'gateway'
  properties: {
    image: '${acr.properties.loginServer}/mcp-gateway:${gatewayImageTag}'
    targetPort: '8000'
    isMain: true
    authType: 'UserAssigned'
    userManagedIdentityClientId: managedIdentity.properties.clientId
    environmentVariables: []
  }
}

// Role assignment: AcrPull (to pull container images)
resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, managedIdentity.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d') // AcrPull
    principalId: managedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Role assignment: Website Contributor (to manage sitecontainers)
resource websiteContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(webApp.id, managedIdentity.id, 'de139f84-1756-47ae-9be6-808fbbe84772')
  scope: webApp
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772') // Website Contributor
    principalId: managedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Role assignment: Cosmos DB Data Contributor (for Cosmos access)
// Cosmos DB uses its own SQL role assignment API
resource cosmosRoleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, managedIdentity.id, 'cosmos-data-contributor')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002' // Cosmos DB Built-in Data Contributor
    principalId: managedIdentity.properties.principalId
    scope: cosmosAccount.id
  }
}

// Outputs
output webAppName string = webApp.name
output webAppHostname string = webApp.properties.defaultHostName
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output acrLoginServer string = acr.properties.loginServer
output acrName string = acr.name
output cosmosEndpoint string = cosmosAccount.properties.documentEndpoint
output managedIdentityClientId string = managedIdentity.properties.clientId
output managedIdentityPrincipalId string = managedIdentity.properties.principalId

output nextSteps string = '''
Next steps to complete deployment:
1. Build and push gateway image:
   az acr build -r ${acrName} -t mcp-gateway:latest ./dotnet

2. Restart the web app to pick up the new image:
   az webapp restart -g exp-mcp-gateway -n ${baseName}-gateway

3. Test the gateway:
   curl https://${baseName}-gateway.azurewebsites.net/adapters
'''
