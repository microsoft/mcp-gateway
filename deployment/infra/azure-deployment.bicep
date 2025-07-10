// Parameters
@description('The Entra ID client ID used for authentication.')
param clientId string

@minLength(3)
@maxLength(30)
@description('Optional suffix used for naming Azure resources and as the public DNS label. Must be alphanumeric and lowercase. If not provided, one is derived from the resource group name.')
param resourceLabel string = resourceGroup().name

@description('The Azure region for resource deployment. Defaults to the resource group location.')
param location string = resourceGroup().location

var aksName = 'mg-aks-${resourceLabel}'
var acrName = 'mgreg${resourceLabel}'
var cosmosDbAccountName = 'mg-storage-${resourceLabel}'
var userAssignedIdentityName = 'mg-identity-${resourceLabel}'
var appInsightsName = 'mg-ai-${resourceLabel}'
var vnetName = 'mg-vnet-${resourceLabel}'
var aksSubnetName = 'mg-aks-subnet-${resourceLabel}'
var appGwSubnetName = 'mg-aag-subnet-${resourceLabel}'
var appGwName = 'mg-aag-${resourceLabel}'
var publicIpName = 'mg-pip-${resourceLabel}'
var federatedCredName = 'mg-sa-federation-${resourceLabel}'

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
      domainNameLabel: resourceLabel
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

// User Assigned Identity for amdin
resource uaiAdmin 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${userAssignedIdentityName}-admin'
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
