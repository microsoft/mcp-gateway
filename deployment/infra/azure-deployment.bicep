// Parameters
@description('The Entra ID client ID used for authentication.')
param clientId string

@minLength(3)
@maxLength(30)
@description('Optional suffix used for naming Azure resources and as the public DNS label. Must be alphanumeric and lowercase. If not provided, one is derived from the resource group name.')
param resourceLabel string = resourceGroup().name

@description('The Azure region for resource deployment. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Enable a private endpoint and DNS link for Cosmos DB and disable its public access. Does not configure private ACR access.')
param enablePrivateEndpoints bool = false

@description('Enable Kubernetes deployment script. Set to false when using external PowerShell script for deployment.')
param enableKubernetesDeploymentScript bool = true

@minLength(1)
@maxLength(63)
@description('Kubernetes namespace used by workloads, gateway routing, and workload identity service accounts.')
param kubernetesNamespace string = 'adapter'

@minValue(1)
@description('AKS node count. Use one only for an isolated short-lived test.')
param nodeCount int = 2

@description('AKS system-pool VM size; must meet current AKS system-node requirements.')
param nodeVmSize string = 'Standard_D4ds_v5'

@allowed(['Basic', 'Standard', 'Premium'])
param acrSku string = 'Standard'

@description('Use consumption-based Cosmos DB for a new test account. Not an in-place account conversion.')
param cosmosServerless bool = false

@secure()
@description('Base64 PFX certificate for the public HTTPS listener. Supply for authenticated cloud access.')
param tlsCertificateData string = ''

@secure()
param tlsCertificatePassword string = ''

@description('Exact gateway image to deploy; set to the image built from the checkout being validated.')
param gatewayImage string = 'ghcr.io/microsoft/mcp-gateway:latest'

@description('Exact first-party tool gateway image to deploy.')
param toolGatewayImage string = 'ghcr.io/microsoft/tool-gateway:latest'

@secure()
@description('Optional first-party identity-forwarding secret; never supplied to adapter pods. The embedded deployment reuses the stored secret, or generates one on first deployment when omitted. An explicit value must match any existing secret.')
param gatewaySecret string = ''

var tlsEnabled = !empty(tlsCertificateData)
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
    name: acrSku
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
        count: nodeCount
        vmSize: nodeVmSize
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
      networkPolicy: 'azure'
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
    globalConfiguration: {
      enableRequestBuffering: false
      enableResponseBuffering: false
    }
    sslCertificates: tlsEnabled ? [
      {
        name: 'gateway-tls'
        properties: {
          data: tlsCertificateData
          password: tlsCertificatePassword
        }
      }
    ] : []
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
          port: tlsEnabled ? 443 : 80
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
          requestTimeout: 600
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
          protocol: tlsEnabled ? 'Https' : 'Http'
          sslCertificate: tlsEnabled ? {
            id: resourceId('Microsoft.Network/applicationGateways/sslCertificates', appGwName, 'gateway-tls')
          } : null
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
    subject: 'system:serviceaccount:${kubernetesNamespace}:mcpgateway-sa'
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
    subject: 'system:serviceaccount:${kubernetesNamespace}:workload-sa'
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
    capabilities: cosmosServerless ? [{ name: 'EnableServerless' }] : []
    disableLocalAuth: true
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

resource kubernetesDeployment 'Microsoft.Resources/deploymentScripts@2023-08-01' = if (enableKubernetesDeploymentScript) {
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
      set -euo pipefail
      if [[ ! "$KUBERNETES_NAMESPACE" =~ ^[a-z0-9]([-a-z0-9]*[a-z0-9])?$ ]]; then
        echo 'Invalid Kubernetes namespace.' >&2
        exit 1
      fi

      secret_result=$(az aks command invoke --resource-group "$ResourceGroupName" --name "$AKS_NAME" --command "kubectl -n $KUBERNETES_NAMESPACE get secret gateway-secret -o json --ignore-not-found" --output json)
      if [[ $(printf '%s' "$secret_result" | jq -r '.exitCode') != 0 ]]; then
        echo 'Could not safely inspect the existing gateway secret.' >&2
        exit 1
      fi
      existing_secret=$(printf '%s' "$secret_result" | jq -r '.logs // ""')
      if [[ -n "${existing_secret//[[:space:]]/}" ]]; then
        GATEWAY_SECRET_BASE64=$(printf '%s' "$existing_secret" | jq -er '.data.gatewaySecret | select(type == "string" and length > 0)')
        if [[ -n "$GATEWAY_SECRET" && $(printf '%s' "$GATEWAY_SECRET" | base64 --wrap=0) != "$GATEWAY_SECRET_BASE64" ]]; then
          echo 'The supplied gateway secret does not match the existing secret. Rotate it separately before redeploying.' >&2
          exit 1
        fi
      else
        if [[ -z "$GATEWAY_SECRET" ]]; then
          GATEWAY_SECRET=$(openssl rand -base64 32)
        fi
        GATEWAY_SECRET_BASE64=$(printf '%s' "$GATEWAY_SECRET" | base64 --wrap=0)
      fi
      if [[ $(printf '%s' "$GATEWAY_SECRET_BASE64" | base64 --decode | tr -d '[:space:]' | wc -c) -eq 0 ]]; then
        echo 'The gateway secret must not be empty.' >&2
        exit 1
      fi

      trap 'rm -f cloud-deployment-template.yml' EXIT
      printf '%s' "$KUBERNETES_TEMPLATE" | base64 --decode > cloud-deployment-template.yml
      sed -i "s|\${AZURE_CLIENT_ID}|$AZURE_CLIENT_ID|g" cloud-deployment-template.yml
      sed -i "s|\${WORKLOAD_CLIENT_ID}|$WORKLOAD_CLIENT_ID|g" cloud-deployment-template.yml
      sed -i "s|\${TENANT_ID}|$TENANT_ID|g" cloud-deployment-template.yml
      sed -i "s|\${CLIENT_ID}|$CLIENT_ID|g" cloud-deployment-template.yml
      sed -i "s|\${APPINSIGHTS_CONNECTION_STRING}|$APPINSIGHTS_CONNECTION_STRING|g" cloud-deployment-template.yml
      sed -i "s|\${IDENTIFIER}|$IDENTIFIER|g" cloud-deployment-template.yml
      sed -i "s|\${REGION}|$REGION|g" cloud-deployment-template.yml
      sed -i "s|\${GATEWAY_IMAGE}|$GATEWAY_IMAGE|g" cloud-deployment-template.yml
      sed -i "s|\${TOOL_GATEWAY_IMAGE}|$TOOL_GATEWAY_IMAGE|g" cloud-deployment-template.yml
      sed -i "s|\${KUBERNETES_NAMESPACE}|$KUBERNETES_NAMESPACE|g" cloud-deployment-template.yml
      sed -i "s|\${PUBLIC_ORIGIN}|$PUBLIC_ORIGIN|g" cloud-deployment-template.yml
      sed -i "s|\${GATEWAY_SECRET_BASE64}|$GATEWAY_SECRET_BASE64|g" cloud-deployment-template.yml
      if grep -Eq '\$\{[A-Z_]+\}' cloud-deployment-template.yml; then
        echo 'Unresolved placeholder in Kubernetes manifest.' >&2
        exit 1
      fi

      apply_result=$(az aks command invoke --resource-group "$ResourceGroupName" --name "$AKS_NAME" --command 'kubectl apply -f cloud-deployment-template.yml' --file cloud-deployment-template.yml --output json)
      if [[ $(printf '%s' "$apply_result" | jq -r '.exitCode') != 0 ]]; then
        echo 'Kubernetes apply failed.' >&2
        exit 1
      fi
    '''
    environmentVariables: [
      {
        name: 'KUBERNETES_TEMPLATE'
        value: base64(loadTextContent('../k8s/cloud-deployment-template.yml'))
      }
      {
        name: 'KUBERNETES_NAMESPACE'
        value: kubernetesNamespace
      }
      {
        name: 'AKS_NAME'
        value: aksName
      }
      {
        name: 'GATEWAY_IMAGE'
        value: gatewayImage
      }
      {
        name: 'TOOL_GATEWAY_IMAGE'
        value: toolGatewayImage
      }
      {
        name: 'PUBLIC_ORIGIN'
        value: '${tlsEnabled ? 'https' : 'http'}://${publicIpDnsLabel}.${location}.cloudapp.azure.com/'
      }
      {
        name: 'GATEWAY_SECRET'
        secureValue: gatewaySecret
      }
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

// Outputs
output aksName string = aksName
output acrName string = acrName
output cosmosDbAccountName string = cosmosDbAccountName
output userAssignedIdentityClientId string = uai.properties.clientId
output workloadIdentityClientId string = uaiWorkload.properties.clientId
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output resourceGroupName string = resourceGroup().name
output resourceLabel string = resourceLabel
output kubernetesNamespace string = kubernetesNamespace
output tenantId string = tenant().tenantId
output location string = location
output publicIpFqdn string = appGwPublicIp.properties.dnsSettings.fqdn
output publicOrigin string = '${tlsEnabled ? 'https' : 'http'}://${appGwPublicIp.properties.dnsSettings.fqdn}/'
