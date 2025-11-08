// Parameters
@description('The Entra ID client ID used for authentication.')
param clientId string

@minLength(3)
@maxLength(30)
@description('Optional suffix used for naming Azure resources and as the public DNS label. Must be alphanumeric and lowercase. If not provided, one is derived from the resource group name.')
param resourceLabel string = resourceGroup().name

@description('The Azure region for resource deployment. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Enable private endpoints for Azure resources (ACR, Cosmos DB). When enabled, resources will only be accessible within the VNet.')
param enablePrivateEndpoints bool = false

var resourceLabelLower = toLower(resourceLabel)

var aksNameBase = 'mg-aks-${resourceLabelLower}'
var aksName = substring(aksNameBase, 0, min(length(aksNameBase), 63))

var acrNameBase = 'mgreg${resourceLabelLower}'
var acrName = substring(acrNameBase, 0, min(length(acrNameBase), 50))

var cosmosDbAccountNameBase = 'mg-storage-${resourceLabelLower}'
var cosmosDbAccountName = substring(cosmosDbAccountNameBase, 0, min(length(cosmosDbAccountNameBase), 44))

var userAssignedIdentityNameBase = 'mg-identity-${resourceLabelLower}'
var userAssignedIdentityName = substring(userAssignedIdentityNameBase, 0, min(length(userAssignedIdentityNameBase), 128))

var appInsightsNameBase = 'mg-ai-${resourceLabelLower}'
var appInsightsName = substring(appInsightsNameBase, 0, min(length(appInsightsNameBase), 260))

var vnetNameBase = 'mg-vnet-${resourceLabelLower}'
var vnetName = substring(vnetNameBase, 0, min(length(vnetNameBase), 64))

var aksSubnetNameBase = 'mg-aks-subnet-${resourceLabelLower}'
var aksSubnetName = substring(aksSubnetNameBase, 0, min(length(aksSubnetNameBase), 80))

var appGwSubnetNameBase = 'mg-aag-subnet-${resourceLabelLower}'
var appGwSubnetName = substring(appGwSubnetNameBase, 0, min(length(appGwSubnetNameBase), 80))

var peSubnetNameBase = 'mg-pe-subnet-${resourceLabelLower}'
var peSubnetName = substring(peSubnetNameBase, 0, min(length(peSubnetNameBase), 80))

var appGwNameBase = 'mg-aag-${resourceLabelLower}'
var appGwName = substring(appGwNameBase, 0, min(length(appGwNameBase), 80))

var publicIpNameBase = 'mg-pip-${resourceLabelLower}'
var publicIpName = substring(publicIpNameBase, 0, min(length(publicIpNameBase), 80))

var publicIpDnsLabel = substring(resourceLabelLower, 0, min(length(resourceLabelLower), 63))

var federatedCredNameBase = 'mg-sa-federation-${resourceLabelLower}'
var federatedCredName = substring(federatedCredNameBase, 0, min(length(federatedCredNameBase), 128))

var federatedCredWorkloadNameBase = '${federatedCredName}-workload'
var federatedCredWorkloadName = substring(federatedCredWorkloadNameBase, 0, min(length(federatedCredWorkloadNameBase), 128))

// VNet
resource vnet 'Microsoft.Network/virtualNetworks@2022-07-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.0.0.0/16'
      ]
    }
    subnets: [
      {
        name: aksSubnetName
        properties: {
          addressPrefix: '10.0.1.0/24'
        }
      }
      {
        name: appGwSubnetName
        properties: {
          addressPrefix: '10.0.2.0/24'
        }
      }
      {
        name: peSubnetName
        properties: {
          addressPrefix: '10.0.3.0/24'
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource networkContributorRA 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vnet.name, aks.name, aksSubnetName, 'network-contributor')
  scope: vnet
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4d97b98b-1d4f-4787-a291-c67834d212e7') // Network Contributor
    principalId: aks.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ACR
resource acr 'Microsoft.ContainerRegistry/registries@2023-01-01-preview' = {
  name: acrName
  location: location
  sku: {
    name: 'Standard'
  }
  properties: {
    policies: {
      quarantinePolicy: {
        status: 'disabled'
      }
    }
    publicNetworkAccess: 'Enabled'
    anonymousPullEnabled: false
    networkRuleBypassOptions: 'AzureServices'
    dataEndpointEnabled: false
    adminUserEnabled: false
  }
}

// AKS Cluster
resource aks 'Microsoft.ContainerService/managedClusters@2023-04-01' = {
  name: aksName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    dnsPrefix: aksName
    agentPoolProfiles: [
      {
        name: 'nodepool1'
        count: 2
        vmSize: 'Standard_D4ds_v5'
        osType: 'Linux'
        mode: 'System'
        osSKU: 'Ubuntu'
        vnetSubnetID: resourceId('Microsoft.Network/virtualNetworks/subnets', vnetName, aksSubnetName)
      }
    ]
    enableRBAC: true
    aadProfile: {
      managed: true
      enableAzureRBAC: true
    }
    networkProfile: {
      networkPlugin: 'azure'
      loadBalancerSku: 'standard'
      serviceCidr: '192.168.0.0/16'
      dnsServiceIP: '192.168.0.10'
    }
    addonProfiles: {}
    apiServerAccessProfile: {
      enablePrivateCluster: false
    }
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
  }
  dependsOn: [vnet]
}


// Attach ACR to AKS
resource acrRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aks.id, acr.id, 'acrpull')
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d') // AcrPull
    principalId: aks.properties.identityProfile.kubeletidentity.objectId
    principalType: 'ServicePrincipal'
  }
}

// Public IP for App Gateway
resource appGwPublicIp 'Microsoft.Network/publicIPAddresses@2022-05-01' = {
  name: publicIpName
  location: location
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
    dnsSettings: {
      domainNameLabel: publicIpDnsLabel
    }
  }
}

// Application Gateway
resource appGw 'Microsoft.Network/applicationGateways@2022-09-01' = {
  name: appGwName
  location: location
  properties: {
    sku: {
      name: 'Standard_v2'
      tier: 'Standard_v2'
      capacity: 1
    }
    gatewayIPConfigurations: [
      {
        name: 'appGwIpConfig'
        properties: {
          subnet: {
            id: resourceId('Microsoft.Network/virtualNetworks/subnets', vnetName, appGwSubnetName)
          }
        }
      }
    ]
    frontendIPConfigurations: [
      {
        name: 'appGwFrontendIP'
        properties: {
           publicIPAddress: {
            id: appGwPublicIp.id
          }
        }
      }
    ]
    frontendPorts: [
      {
        name: 'httpPort'
        properties: {
          port: 80
        }
      }
    ]
    backendAddressPools: [
      {
        name: 'aksBackendPool'
        properties: {
          backendAddresses: [
            {
              ipAddress: '10.0.1.100'
            }
          ]
        }
      }
    ]
    backendHttpSettingsCollection: [
      {
        name: 'httpSettings'
        properties: {
          port: 8000
          protocol: 'Http'
          pickHostNameFromBackendAddress: false
          requestTimeout: 20
          probe: {
            id: resourceId('Microsoft.Network/applicationGateways/probes', appGwName, 'mcpgateway-probe')
          }
        }
      }
    ]
    httpListeners: [
      {
        name: 'httpListener'
        properties: {
          frontendIPConfiguration: {
            id: resourceId('Microsoft.Network/applicationGateways/frontendIPConfigurations', appGwName, 'appGwFrontendIP')
          }
          frontendPort: {
            id: resourceId('Microsoft.Network/applicationGateways/frontendPorts', appGwName, 'httpPort')
          }
          protocol: 'Http'
        }
      }
    ]
    requestRoutingRules: [
      {
        name: 'rule1'
        properties: {
          ruleType: 'Basic'
          httpListener: {
            id: resourceId('Microsoft.Network/applicationGateways/httpListeners', appGwName, 'httpListener')
          }
          backendAddressPool: {
            id: resourceId('Microsoft.Network/applicationGateways/backendAddressPools', appGwName, 'aksBackendPool')
          }
          backendHttpSettings: {
            id: resourceId('Microsoft.Network/applicationGateways/backendHttpSettingsCollection', appGwName, 'httpSettings')
          }
          priority: 100
        }
      }
    ]
    probes: [
    {
      name: 'mcpgateway-probe'
      properties: {
        protocol: 'Http'
        host: '10.0.1.100'
        path: '/ping'
        interval: 30
        timeout: 30
        unhealthyThreshold: 3
        pickHostNameFromBackendHttpSettings: false
        minServers: 0
        match: {
          statusCodes: [
            '200-399'
          ]
        }
      }
    }
    ]
  }
  dependsOn: [vnet]
}


// User Assigned Identity
resource uai 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: userAssignedIdentityName
  location: location
}

// User Assigned Identity for admin
resource uaiAdmin 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${userAssignedIdentityName}-admin'
  location: location
}

// User Assigned Identity for server workload instance
resource uaiWorkload 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${userAssignedIdentityName}-workload'
  location: location
}

resource uaiAdminContributorOnAks 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aks.name, uaiAdmin.name, 'AKSContributor')
  scope: aks
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ed7f3fbd-7b88-4dd4-9017-9adb7ce333f8') // AKS Contributor
    principalId: uaiAdmin.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource uaiAdminRbacClusterAdminOnAks 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aks.name, uaiAdmin.name, 'AKSRBACAdmin')
  scope: aks
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b1ff04bb-8a4e-4dc4-8eb5-8693973ce19b') // AKS Service RBAC Cluster Admin
    principalId: uaiAdmin.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Federated Credential
resource federatedCred 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: uai
  name: federatedCredName
  properties: {
    audiences: [
      'api://AzureADTokenExchange'
    ]
    issuer: aks.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:adapter:mcpgateway-sa'
  }
}

// Federated Credential
resource federatedCredWorkload 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: uaiWorkload
  name: federatedCredWorkloadName
  properties: {
    audiences: [
      'api://AzureADTokenExchange'
    ]
    issuer: aks.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:adapter:workload-sa'
  }
}

// CosmosDB Account
resource cosmosDb 'Microsoft.DocumentDB/databaseAccounts@2023-04-15' = {
  name: cosmosDbAccountName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    locations: [
      {
        locationName: location
        failoverPriority: 0
      }
    ]
    capabilities: []
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    enableFreeTier: false
    publicNetworkAccess: enablePrivateEndpoints ? 'Disabled' : 'Enabled'
  }
}

// Cosmos DB SQL Database
resource cosmosDbSqlDb 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2023-04-15' = {
  parent: cosmosDb
  name: 'McpGatewayDb'
  properties: {
    resource: {
      id: 'McpGatewayDb'
    }
  }
}

// Cosmos DB SQL Containers
resource adapterContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = {
  name: 'AdapterContainer'
  parent: cosmosDbSqlDb
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

resource cacheContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = {
  name: 'CacheContainer'
  parent: cosmosDbSqlDb
  properties: {
    resource: {
      id: 'CacheContainer'
      partitionKey: {
        paths: ['/id']
        kind: 'Hash'
      }
    }
  }
}

resource toolContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = {
  name: 'ToolContainer'
  parent: cosmosDbSqlDb
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

// Cosmos DB Data Contributor Role Assignment to UAI
resource cosmosDbRoleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2022-11-15' = {
  parent: cosmosDb
  name: guid(cosmosDb.name, uai.id, 'data-contributor')
  properties: {
    roleDefinitionId: resourceId('Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions', cosmosDb.name, '00000000-0000-0000-0000-000000000002')
    principalId: uai.properties.principalId
    scope: cosmosDb.id
  }
}

// Private DNS Zone for Cosmos DB
resource cosmosPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = if (enablePrivateEndpoints) {
  name: 'privatelink.documents.azure.com'
  location: 'global'
}

// Link Private DNS Zone to VNet
resource cosmosDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = if (enablePrivateEndpoints) {
  parent: cosmosPrivateDnsZone
  name: '${vnetName}-cosmos-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

// Private Endpoint for Cosmos DB
resource cosmosPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-04-01' = if (enablePrivateEndpoints) {
  name: 'pe-${cosmosDbAccountName}'
  location: location
  properties: {
    subnet: {
      id: resourceId('Microsoft.Network/virtualNetworks/subnets', vnetName, peSubnetName)
    }
    privateLinkServiceConnections: [
      {
        name: 'pe-${cosmosDbAccountName}-connection'
        properties: {
          privateLinkServiceId: cosmosDb.id
          groupIds: [
            'Sql'
          ]
        }
      }
    ]
  }
  dependsOn: [vnet]
}

resource cosmosPrivateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-04-01' = if (enablePrivateEndpoints) {
  parent: cosmosPrivateEndpoint
  name: 'cosmos-dns-zone-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink-documents-azure-com'
        properties: {
          privateDnsZoneId: cosmosPrivateDnsZone.id
        }
      }
    ]
  }
}

// Application Insights
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
  }
}

resource kubernetesDeployment 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${uaiAdmin.id}': {}
    }
  }
  name: 'kubernetesDeployment'
  location: location
  kind: 'AzureCLI'
  properties: {
    azCliVersion: '2.60.0'
    timeout: 'PT30M'
    retentionInterval: 'P1D'
    scriptContent: '''
      sed -i "s|\${AZURE_CLIENT_ID}|$AZURE_CLIENT_ID|g" cloud-deployment-template.yml
      sed -i "s|\${WORKLOAD_CLIENT_ID}|$WORKLOAD_CLIENT_ID|g" cloud-deployment-template.yml
      sed -i "s|\${TENANT_ID}|$TENANT_ID|g" cloud-deployment-template.yml
      sed -i "s|\${CLIENT_ID}|$CLIENT_ID|g" cloud-deployment-template.yml
      sed -i "s|\${APPINSIGHTS_CONNECTION_STRING}|$APPINSIGHTS_CONNECTION_STRING|g" cloud-deployment-template.yml
      sed -i "s|\${IDENTIFIER}|$IDENTIFIER|g" cloud-deployment-template.yml
      sed -i "s|\${REGION}|$REGION|g" cloud-deployment-template.yml

      az aks command invoke -g $ResourceGroupName -n mg-aks-"$ResourceGroupName" --command "kubectl apply -f cloud-deployment-template.yml" --file cloud-deployment-template.yml
    '''
    supportingScriptUris: [
      'https://raw.githubusercontent.com/microsoft/mcp-gateway/refs/heads/main/deployment/k8s/cloud-deployment-template.yml'
    ]
    environmentVariables: [
      {
        name: 'REGION'
        value: location
      }
      {
        name: 'CLIENT_ID'
        value: clientId
      }
      {
        name: 'AZURE_CLIENT_ID'
        value: uai.properties.clientId
      }
      {
        name: 'WORKLOAD_CLIENT_ID'
        value: uaiWorkload.properties.clientId
      }
      {
        name: 'APPINSIGHTS_CONNECTION_STRING'
        value: appInsights.properties.ConnectionString
      }
      {
        name: 'ResourceGroupName'
        value: resourceGroup().name
      }
      {
        name: 'IDENTIFIER'
        value: resourceLabel
      }
      {
        name: 'TENANT_ID'
        value: tenant().tenantId
      }
    ]
  }
  dependsOn: [aks]
}
