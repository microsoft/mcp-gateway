<#
.SYNOPSIS
    Deploys the MCP Gateway infrastructure to Azure and configures Kubernetes resources.

.DESCRIPTION
    This script performs a two-step deployment:
    1. Deploys Azure infrastructure using Bicep (AKS, ACR, Cosmos DB, etc.)
    2. Configures Kubernetes resources on the deployed AKS cluster

    The script supports both full deployment and infrastructure-only deployment.

.PARAMETER ResourceGroupName
    The name of the Azure resource group to deploy to. Will be created if it doesn't exist.

.PARAMETER ClientId
    The Entra ID client ID used for authentication.

.PARAMETER ResourceLabel
    Optional suffix for naming Azure resources. Must be alphanumeric and lowercase (3-30 characters).
    If not provided, derived from the resource group name.

.PARAMETER Location
    The Azure region for resource deployment. Defaults to 'westus3'.

.PARAMETER EnablePrivateEndpoints
    Enable private endpoints for Azure resources (ACR, Cosmos DB). When enabled, resources will only be accessible within the VNet.

.EXAMPLE
    .\Deploy-McpGateway.ps1 -ResourceGroupName "rg-mcpgateway-dev" -ClientId "00000000-0000-0000-0000-000000000000"

.EXAMPLE
    .\Deploy-McpGateway.ps1 -ResourceGroupName "rg-mcpgateway-prod" -ClientId "00000000-0000-0000-0000-000000000000" -ResourceLabel "mcpprod" -Location "westus2" -EnablePrivateEndpoints

.NOTES
    Prerequisites:
    - Azure CLI installed and authenticated (az login)
    - Appropriate Azure permissions to create resources
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ResourceGroupName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ClientId,

    [Parameter(Mandatory = $false)]
    [ValidateLength(3, 30)]
    [ValidatePattern('^[a-z0-9]+$')]
    [string]$ResourceLabel,

    [Parameter(Mandatory = $false)]
    [string]$Location = "westus3",

    [Parameter(Mandatory = $false)]
    [switch]$EnablePrivateEndpoints,

    [Parameter(Mandatory = $false)]
    [ValidateSet('AzureCloud', 'AzureUSGovernment')]
    [string]$CloudEnvironment = "AzureCloud"
)

# Error handling
$ErrorActionPreference = "Stop"

# Function to write colored output
function Write-ColorOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message,
        
        [Parameter(Mandatory = $false)]
        [ValidateSet('Info', 'Success', 'Warning', 'Error')]
        [string]$Type = 'Info'
    )

    $color = switch ($Type) {
        'Info' { 'Cyan' }
        'Success' { 'Green' }
        'Warning' { 'Yellow' }
        'Error' { 'Red' }
    }

    Write-Host $Message -ForegroundColor $color
}

# Function to check prerequisites
function Test-Prerequisites {
    Write-ColorOutput "Checking prerequisites..." -Type Info

    # Check Azure CLI
    try {
        $azVersion = az version --output json | ConvertFrom-Json
        Write-ColorOutput "✓ Azure CLI version: $($azVersion.'azure-cli')" -Type Success
    }
    catch {
        Write-ColorOutput "✗ Azure CLI is not installed or not in PATH" -Type Error
        throw "Please install Azure CLI: https://docs.microsoft.com/en-us/cli/azure/install-azure-cli"
    }

    # Check Azure CLI login
    try {
        $account = az account show --output json 2>$null | ConvertFrom-Json
        if (-not $account) {
            throw "Not logged in"
        }
        Write-ColorOutput "✓ Logged in to Azure as: $($account.user.name)" -Type Success
    }
    catch {
        Write-ColorOutput "✗ Not logged in to Azure CLI" -Type Error
        throw "Please run 'az login' first"
    }

    # Set Azure cloud environment
    if ($CloudEnvironment -ne 'AzureCloud') {
        Write-ColorOutput "Setting Azure cloud environment to: $CloudEnvironment" -Type Info
        az cloud set --name $CloudEnvironment
        Write-ColorOutput "✓ Cloud environment set to $CloudEnvironment" -Type Success
    }
}

# Main deployment function
function Start-Deployment {
    Write-ColorOutput "`n========================================" -Type Info
    Write-ColorOutput "MCP Gateway Deployment" -Type Info
    Write-ColorOutput "========================================`n" -Type Info

    # Validate location for the target cloud environment
    $govRegions = @('usgovvirginia', 'usgovtexas', 'usgovarizona', 'usdodeast', 'usdodcentral')
    if ($CloudEnvironment -eq 'AzureUSGovernment' -and $Location -notin $govRegions) {
        Write-ColorOutput "Location '$Location' is not a valid Azure Government region." -Type Error
        Write-ColorOutput "Valid regions: $($govRegions -join ', ')" -Type Error
        throw "Please specify a valid Azure Government region using the -Location parameter."
    }

    # Check prerequisites
    Test-Prerequisites

    # Get current subscription
    $currentSubscription = az account show --output json | ConvertFrom-Json
    Write-ColorOutput "Using subscription: $($currentSubscription.name) ($($currentSubscription.id))" -Type Info

    # Set template file path
    $TemplateFile = Join-Path $PSScriptRoot "infra" "azure-deployment.bicep"

    if (-not (Test-Path $TemplateFile)) {
        Write-ColorOutput "Template file not found: $TemplateFile" -Type Error
        throw "Bicep template file not found"
    }

    Write-ColorOutput "Using template: $TemplateFile`n" -Type Info

    # Create resource group if it doesn't exist
    Write-ColorOutput "Checking resource group: $ResourceGroupName" -Type Info
    $rgExists = az group exists --name $ResourceGroupName
    if ($rgExists -eq "false") {
        Write-ColorOutput "Creating resource group: $ResourceGroupName in $Location" -Type Info
        az group create --name $ResourceGroupName --location $Location --output none
        Write-ColorOutput "✓ Resource group created" -Type Success
    }
    else {
        Write-ColorOutput "✓ Resource group exists" -Type Success
    }

    # Build deployment parameters
    $deploymentParams = @()
    $deploymentParams += "clientId=$ClientId"
    $deploymentParams += "location=$Location"
    $deploymentParams += "enablePrivateEndpoints=$($EnablePrivateEndpoints.IsPresent.ToString().ToLower())"
    $deploymentParams += "enableKubernetesDeploymentScript=false"

    if ($ResourceLabel) {
        $deploymentParams += "resourceLabel=$ResourceLabel"
    }

    # Deploy Bicep template
    Write-ColorOutput "`nStarting Bicep deployment..." -Type Info
    Write-ColorOutput "This may take 15-20 minutes...`n" -Type Warning

    $deploymentName = "mcpgateway-$(Get-Date -Format 'yyyyMMddHHmmss')"
    
    try {
        $deploymentResult = az deployment group create `
            --name $deploymentName `
            --resource-group $ResourceGroupName `
            --template-file $TemplateFile `
            --parameters $deploymentParams `
            --output json
        
        if ($LASTEXITCODE -ne 0) {
            throw "Bicep deployment failed with exit code $LASTEXITCODE"
        }
        
        $deployment = $deploymentResult | ConvertFrom-Json

        Write-ColorOutput "`n✓ Bicep deployment completed successfully" -Type Success
    }
    catch {
        Write-ColorOutput "`n✗ Bicep deployment failed" -Type Error
        throw
    }

    # Extract outputs
    $outputs = $deployment.properties.outputs

    Write-ColorOutput "`n========================================" -Type Info
    Write-ColorOutput "Deployment Outputs" -Type Info
    Write-ColorOutput "========================================" -Type Info
    Write-ColorOutput "AKS Cluster Name: $($outputs.aksName.value)" -Type Info
    Write-ColorOutput "ACR Name: $($outputs.acrName.value)" -Type Info
    Write-ColorOutput "Cosmos DB Account: $($outputs.cosmosDbAccountName.value)" -Type Info
    Write-ColorOutput "Public FQDN: $($outputs.publicIpFqdn.value)" -Type Info
    Write-ColorOutput "Resource Label: $($outputs.resourceLabel.value)" -Type Info
    Write-ColorOutput "========================================`n" -Type Info

    # Deploy to Kubernetes
    Write-ColorOutput "Starting Kubernetes deployment..." -Type Info
    Deploy-KubernetesResources -Outputs $outputs -ResourceGroupName $ResourceGroupName -ClientId $ClientId

    Write-ColorOutput "`n✓ Deployment completed successfully!" -Type Success
    Write-ColorOutput "`nAccess your deployment at: http://$($outputs.publicIpFqdn.value)" -Type Success
}

# Function to deploy Kubernetes resources
function Deploy-KubernetesResources {
    param(
        [Parameter(Mandatory = $true)]
        $Outputs,

        [Parameter(Mandatory = $true)]
        [string]$ResourceGroupName,

        [Parameter(Mandatory = $true)]
        [string]$ClientId
    )

    Write-ColorOutput "Configuring Kubernetes resources..." -Type Info

    $aksName = $Outputs.aksName.value
    $azureClientId = $Outputs.userAssignedIdentityClientId.value
    $workloadClientId = $Outputs.workloadIdentityClientId.value
    $appInsightsConnectionString = $Outputs.appInsightsConnectionString.value
    $identifier = $Outputs.resourceLabel.value
    $tenantId = $Outputs.tenantId.value
    $region = $Outputs.location.value
    $azureAdInstance = $Outputs.azureAdInstance.value
    $cosmosEndpoint = $Outputs.cosmosDbEndpoint.value
    $acrLoginServer = $Outputs.acrLoginServer.value
    $publicFqdn = $Outputs.publicIpFqdn.value

    # Download the Kubernetes template
    $templateUrl = "https://raw.githubusercontent.com/microsoft/mcp-gateway/refs/heads/main/deployment/k8s/cloud-deployment-template.yml"
    $tempDir = Join-Path $env:TEMP "mcpgateway-k8s"
    if (-not (Test-Path $tempDir)) {
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    }
    
    $templatePath = Join-Path $tempDir "cloud-deployment-template.yml"
    
    Write-ColorOutput "Downloading Kubernetes template..." -Type Info
    try {
        Invoke-WebRequest -Uri $templateUrl -OutFile $templatePath -UseBasicParsing
        Write-ColorOutput "✓ Template downloaded" -Type Success
    }
    catch {
        Write-ColorOutput "✗ Failed to download template" -Type Error
        throw
    }

    # Read and replace placeholders
    Write-ColorOutput "Processing template..." -Type Info
    $content = Get-Content $templatePath -Raw
    
    $replacements = @{
        '${AZURE_CLIENT_ID}' = $azureClientId
        '${WORKLOAD_CLIENT_ID}' = $workloadClientId
        '${TENANT_ID}' = $tenantId
        '${CLIENT_ID}' = $ClientId
        '${APPINSIGHTS_CONNECTION_STRING}' = $appInsightsConnectionString
        '${IDENTIFIER}' = $identifier
        '${REGION}' = $region
        '${AZURE_AD_INSTANCE}' = $azureAdInstance
        '${COSMOS_ENDPOINT}' = $cosmosEndpoint
        '${ACR_LOGIN_SERVER}' = $acrLoginServer
        '${PUBLIC_ORIGIN}' = "http://$publicFqdn/"
    }

    foreach ($key in $replacements.Keys) {
        $content = $content -replace [regex]::Escape($key), $replacements[$key]
    }

    $processedTemplatePath = Join-Path $tempDir "cloud-deployment-processed.yml"
    $content | Set-Content $processedTemplatePath -NoNewline

    # Apply Kubernetes manifest using AKS command invoke
    Write-ColorOutput "Applying Kubernetes manifest to AKS..." -Type Info
    try {
        az aks command invoke `
            --resource-group $ResourceGroupName `
            --name $aksName `
            --command "kubectl apply -f cloud-deployment-processed.yml" `
            --file $processedTemplatePath `
            --output json | ConvertFrom-Json | Out-Null
        Write-ColorOutput "✓ Kubernetes resources deployed" -Type Success
    }
    catch {
        Write-ColorOutput "✗ Failed to apply Kubernetes manifest" -Type Error
        throw
    }

    # Clean up temp files
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue

    Write-ColorOutput "`n✓ Kubernetes deployment completed" -Type Success
}

# Run the deployment
try {
    Start-Deployment
}
catch {
    Write-ColorOutput "`n✗ Deployment failed: $_" -Type Error
    exit 1
}
