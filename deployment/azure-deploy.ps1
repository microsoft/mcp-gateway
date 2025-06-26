param(
    [Parameter(Mandatory=$true)]
    [string]$ResourceGroupName,

    [Parameter(Mandatory=$true)]
    [string]$ClientId,

    [string]$Location = "westus3"
)

$TenantId = az account show --query tenantId --output tsv

# Create Resource Group
az group create --name "$ResourceGroupName" --location "$Location"

# Deploy Bicep
$deployment = az deployment group create `
  --resource-group $ResourceGroupName `
  --template-file deployment/infra/azure-deployment.bicep | ConvertFrom-Json

# Set cloud-deployment.yml
(Get-Content -Raw "deployment/k8s/cloud-deployment-template.yml") `
    -replace "{IDENTIFIER}", $ResourceGroupName `
    -replace "{TENANT_ID}", $TenantId `
    -replace "{CLIENT_ID}", $ClientId `
    -replace "{AZURE_CLIENT_ID}",  $deployment.properties.outputs.identityClientId.value `
    -replace "{APPINSIGHTS_CONNECTION_STRING}", $deployment.properties.outputs.applicationInsightConnectionString.value `
    | Set-Content "deployment/k8s/cloud-deployment.yml"

# Deploy to AKS
az aks command invoke -g $ResourceGroupName -n mg-aks-"$ResourceGroupName" --command "kubectl apply -f cloud-deployment.yml" --file deployment/k8s/cloud-deployment.yml