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
    Enable the Cosmos DB private endpoint and DNS link, disabling its public access. This does not configure private ACR access.

.PARAMETER SubscriptionId
    Azure subscription to select before deployment.

.PARAMETER TenantId
    Expected tenant ID; deployment stops if the selected subscription belongs to another tenant.

.PARAMETER Stage
    Infrastructure, Kubernetes, or All. Use separate stages to publish images to the new ACR before deploying pods.

.PARAMETER DeploymentName
    ARM deployment name. Reuse the same value for the Kubernetes stage to read infrastructure outputs.

.PARAMETER NodeCount
    AKS node count, default two. One node is intended only for isolated development tests.

.PARAMETER NodeVmSize
    AKS system-node VM size, default Standard_D4ds_v5. Validate regional support and quota before deployment.

.PARAMETER AcrSku
    Registry tier: Basic, Standard, or Premium. Default Standard.

.PARAMETER CosmosServerless
    Use consumption-based Cosmos DB on a new account. Does not convert an existing account.

.PARAMETER GatewayImage
    Exact gateway image reference to use in the Kubernetes manifest.

.PARAMETER ToolGatewayImage
    Exact first-party Tools image reference to use in the Kubernetes manifest.

.PARAMETER SecureParametersFile
    ARM parameter file containing tlsCertificateData and tlsCertificatePassword. Keep it outside source control.

.PARAMETER KubernetesTemplatePath
    Local Kubernetes manifest template; defaults to the checked-out deployment template.

.PARAMETER KubernetesNamespace
    Kubernetes namespace for infrastructure deployment; defaults to adapter. The Kubernetes stage reuses the namespace recorded in the infrastructure outputs.

.EXAMPLE
    .\Deploy-McpGateway.ps1 -ResourceGroupName "rg-mcpgateway-dev" -ResourceLabel "mcpdev" -ClientId "00000000-0000-0000-0000-000000000000" -Stage Infrastructure

.EXAMPLE
    .\Deploy-McpGateway.ps1 -ResourceGroupName "rg-mcpgateway-prod" -ClientId "00000000-0000-0000-0000-000000000000" -ResourceLabel "mcpprod" -Location "westus2" -EnablePrivateEndpoints

.NOTES
    Prerequisites:
    - PowerShell 7.4 or later
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

    [string]$SubscriptionId,
    [string]$TenantId,
    [ValidateSet('All', 'Infrastructure', 'Kubernetes')]
    [string]$Stage = 'All',
    [string]$DeploymentName = 'mcpgateway',
    [ValidateRange(1, 100)]
    [int]$NodeCount = 2,
    [string]$NodeVmSize = 'Standard_D4ds_v5',
    [ValidateSet('Basic', 'Standard', 'Premium')]
    [string]$AcrSku = 'Standard',
    [switch]$CosmosServerless,
    [string]$GatewayImage = 'ghcr.io/microsoft/mcp-gateway:latest',
    [string]$ToolGatewayImage = 'ghcr.io/microsoft/tool-gateway:latest',
    [ValidateLength(1, 63)]
    [ValidatePattern('\A[a-z0-9]([-a-z0-9]*[a-z0-9])?\z')]
    [string]$KubernetesNamespace,
    [string]$SecureParametersFile,
    [string]$KubernetesTemplatePath = (Join-Path $PSScriptRoot 'k8s/cloud-deployment-template.yml')
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

    if ($SubscriptionId) {
        az account set --subscription $SubscriptionId
        if ($LASTEXITCODE -ne 0) { throw 'Could not select the requested subscription.' }
    }

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
        if ($TenantId -and $account.tenantId -ne $TenantId) {
            throw 'The selected subscription does not belong to the requested tenant.'
        }
        Write-ColorOutput "✓ Logged in to Azure as: $($account.user.name)" -Type Success
    }
    catch {
        Write-ColorOutput "✗ Not logged in to Azure CLI" -Type Error
        throw "Please run 'az login' first"
    }
}

# Main deployment function
function Start-Deployment {
    Write-ColorOutput "`n========================================" -Type Info
    Write-ColorOutput "MCP Gateway Deployment" -Type Info
    Write-ColorOutput "========================================`n" -Type Info

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

    if ($Stage -eq 'Kubernetes') {
        $deployment = az deployment group show --subscription $currentSubscription.id --resource-group $ResourceGroupName --name $DeploymentName --output json | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Cannot read the validated infrastructure deployment outputs.' }
        Deploy-KubernetesResources -Outputs $deployment.properties.outputs -ResourceGroupName $ResourceGroupName -ClientId $ClientId
        return
    }

    # Create resource group if it doesn't exist
    Write-ColorOutput "Checking resource group: $ResourceGroupName" -Type Info
    $rgExists = az group exists --subscription $currentSubscription.id --name $ResourceGroupName
    if ($LASTEXITCODE -ne 0) { throw 'Could not check the target resource group.' }
    if ($rgExists -eq "false") {
        Write-ColorOutput "Creating resource group: $ResourceGroupName in $Location" -Type Info
        az group create --subscription $currentSubscription.id --name $ResourceGroupName --location $Location --output none
        if ($LASTEXITCODE -ne 0) { throw 'Resource group creation failed.' }
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
    $deploymentParams += "nodeCount=$NodeCount"
    $deploymentParams += "nodeVmSize=$NodeVmSize"
    $deploymentParams += "acrSku=$AcrSku"
    $deploymentParams += "cosmosServerless=$($CosmosServerless.IsPresent.ToString().ToLowerInvariant())"
    $deploymentParams += "gatewayImage=$GatewayImage"
    $deploymentParams += "toolGatewayImage=$ToolGatewayImage"
    if ($KubernetesNamespace) {
        $deploymentParams += "kubernetesNamespace=$KubernetesNamespace"
    }
    if ($SecureParametersFile) {
        if (-not (Test-Path -LiteralPath $SecureParametersFile)) { throw 'Secure parameter file not found.' }
        $deploymentParams += "@$SecureParametersFile"
    }

    if ($ResourceLabel) {
        $deploymentParams += "resourceLabel=$ResourceLabel"
    }

    # Deploy Bicep template
    Write-ColorOutput "`nStarting Bicep deployment..." -Type Info
    Write-ColorOutput "This may take 15-20 minutes...`n" -Type Warning

    try {
        $deploymentResult = az deployment group create `
            --subscription $currentSubscription.id `
            --name $DeploymentName `
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

    if ($Stage -eq 'Infrastructure') { return }

    # Deploy to Kubernetes
    Write-ColorOutput "Starting Kubernetes deployment..." -Type Info
    Deploy-KubernetesResources -Outputs $outputs -ResourceGroupName $ResourceGroupName -ClientId $ClientId

    Write-ColorOutput "`n✓ Deployment completed successfully!" -Type Success
    Write-ColorOutput "`nAccess your deployment at: $($outputs.publicOrigin.value)" -Type Success
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
    $namespace = $Outputs.kubernetesNamespace.value
    if ([string]::IsNullOrEmpty($namespace)) { $namespace = 'adapter' }
    if ($namespace.Length -gt 63 -or $namespace -cnotmatch '\A[a-z0-9]([-a-z0-9]*[a-z0-9])?\z') {
        throw 'The infrastructure output contains an invalid Kubernetes namespace.'
    }
    if ($KubernetesNamespace -and $KubernetesNamespace -ne $namespace) {
        throw 'KubernetesNamespace must match the infrastructure deployment. Redeploy infrastructure before changing namespaces.'
    }

    if (-not (Test-Path -LiteralPath $KubernetesTemplatePath)) { throw 'Local Kubernetes template not found.' }
    $templatePath = $KubernetesTemplatePath

    # Read and replace placeholders
    Write-ColorOutput "Processing template..." -Type Info
    $content = Get-Content $templatePath -Raw
    $existing = az aks command invoke --resource-group $ResourceGroupName --name $aksName --command "kubectl -n $namespace get secret gateway-secret -o json --ignore-not-found" --output json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $existing.exitCode -ne 0) { throw 'Could not safely inspect the existing gateway secret.' }
    if (-not [string]::IsNullOrWhiteSpace($existing.logs)) {
        $existingSecret = $existing.logs | ConvertFrom-Json
        $sharedSecret = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($existingSecret.data.gatewaySecret))
    }
    else {
        $sharedSecret = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    }
    if ([string]::IsNullOrWhiteSpace($sharedSecret)) {
        throw 'The existing gateway secret is empty. Repair it before deploying workloads.'
    }
    
    $replacements = @{
        '${AZURE_CLIENT_ID}' = $azureClientId
        '${WORKLOAD_CLIENT_ID}' = $workloadClientId
        '${TENANT_ID}' = $tenantId
        '${CLIENT_ID}' = $ClientId
        '${APPINSIGHTS_CONNECTION_STRING}' = $appInsightsConnectionString
        '${IDENTIFIER}' = $identifier
        '${REGION}' = $region
        '${GATEWAY_IMAGE}' = $GatewayImage
        '${TOOL_GATEWAY_IMAGE}' = $ToolGatewayImage
        '${KUBERNETES_NAMESPACE}' = $namespace
        '${PUBLIC_ORIGIN}' = $Outputs.publicOrigin.value
        '${GATEWAY_SECRET_BASE64}' = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($sharedSecret))
    }

    foreach ($key in $replacements.Keys) {
        $content = $content.Replace($key, [string]$replacements[$key])
    }
    if ($content -match '\$\{[A-Z_]+\}') { throw 'Unresolved placeholder in Kubernetes manifest.' }

    # Apply Kubernetes manifest using AKS command invoke
    Write-ColorOutput "Applying Kubernetes manifest to AKS..." -Type Info
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "mcpgateway-k8s-$([Guid]::NewGuid().ToString('N'))"
    try {
        New-Item -ItemType Directory -Path $tempDir | Out-Null
        $processedTemplatePath = Join-Path $tempDir "cloud-deployment-processed.yml"
        $content | Set-Content $processedTemplatePath -NoNewline
        $applyResult = az aks command invoke `
            --resource-group $ResourceGroupName `
            --name $aksName `
            --command "kubectl apply -f cloud-deployment-processed.yml" `
            --file $processedTemplatePath `
            --output json | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $applyResult.exitCode -ne 0) { throw "Kubernetes apply failed: $($applyResult.logs)" }
        Write-ColorOutput "✓ Kubernetes resources deployed" -Type Success
    }
    catch {
        Write-ColorOutput "✗ Failed to apply Kubernetes manifest" -Type Error
        throw
    }
    finally {
        Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

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
