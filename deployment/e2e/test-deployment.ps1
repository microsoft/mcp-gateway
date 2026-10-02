param([string]$BashPath)

$ErrorActionPreference = 'Stop'
$deploymentRoot = Split-Path $PSScriptRoot -Parent
$sourcePath = Join-Path $deploymentRoot 'Deploy-McpGateway.ps1'
$tokens = $null
$parseErrors = $null
$source = [System.Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
$deploymentFunction = $source.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Deploy-KubernetesResources'
}, $false)
. ([scriptblock]::Create($deploymentFunction.Extent.Text))

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Write-ColorOutput {
    param([string]$Message, [string]$Type)
}

function az {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    $command = $arguments[[Array]::IndexOf($arguments, '--command') + 1]
    Assert-True ($arguments[[Array]::IndexOf($arguments, '--name') + 1] -eq 'test-cluster') 'Unexpected AKS target.'
    $script:Commands.Add($command)
    if ($command -like 'kubectl -n * get secret gateway-secret*') {
        Assert-True ($command -eq "kubectl -n $script:ExpectedNamespace get secret gateway-secret -o json --ignore-not-found") 'Secret lookup used the wrong namespace.'
        $logs = if ($null -eq $script:ExistingSecret) { '' } else {
            @{ data = @{ gatewaySecret = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($script:ExistingSecret)) } } | ConvertTo-Json -Compress
        }
        return @{ exitCode = $script:LookupExitCode; logs = $logs } | ConvertTo-Json -Compress
    }
    if ($command -eq 'kubectl apply -f cloud-deployment-processed.yml') {
        $script:ManifestPath = $arguments[[Array]::IndexOf($arguments, '--file') + 1]
        $script:Manifest = Get-Content -LiteralPath $script:ManifestPath -Raw
        return @{ exitCode = $script:ApplyExitCode; logs = 'mock apply result' } | ConvertTo-Json -Compress
    }
    throw "Unexpected Azure command: $command"
}

$KubernetesTemplatePath = Join-Path $deploymentRoot 'k8s/cloud-deployment-template.yml'
$GatewayImage = 'registry.example/gateway:test'
$ToolGatewayImage = 'registry.example/tools:test'
$retainedSecret = "retained`"secret'&\path`nsecond line"
$cases = @(
    @{ Name = 'Default namespace'; Namespace = 'adapter'; Secret = 'existing-secret' }
    @{ Name = 'First custom-namespace deployment'; Namespace = 'review-ns'; Secret = $null }
    @{ Name = 'Numeric namespace stays a YAML string'; Namespace = '123'; Secret = 'existing-secret' }
    @{ Name = 'Boolean-like namespace stays a YAML string'; Namespace = 'true'; Secret = 'existing-secret' }
    @{ Name = 'Reuse exact secret bytes'; Namespace = 'review-ns'; Secret = $retainedSecret }
    @{ Name = 'Legacy infrastructure outputs'; Namespace = $null; Secret = 'existing-secret' }
    @{ Name = 'Reject namespace override'; Namespace = 'review-ns'; Override = 'other-ns'; Error = 'must match the infrastructure' }
    @{ Name = 'Reject invalid namespace output'; Namespace = 'Bad/Namespace'; Error = 'invalid Kubernetes namespace' }
    @{ Name = 'Reject empty stored secret'; Namespace = 'review-ns'; Secret = ''; Error = 'existing gateway secret is empty' }
    @{ Name = 'Reject failed secret lookup'; Namespace = 'review-ns'; LookupExitCode = 1; Error = 'Could not safely inspect' }
    @{ Name = 'Reject failed apply'; Namespace = 'review-ns'; ApplyExitCode = 1; Error = 'Kubernetes apply failed' }
)

foreach ($case in $cases) {
    $KubernetesNamespace = $case.Override
    $script:ExpectedNamespace = if ($case.Namespace) { $case.Namespace } else { 'adapter' }
    $script:ExistingSecret = $case.Secret
    $script:LookupExitCode = [int]$case.LookupExitCode
    $script:ApplyExitCode = [int]$case.ApplyExitCode
    $script:Commands = [Collections.Generic.List[string]]::new()
    $script:Manifest = $null
    $script:ManifestPath = $null
    $outputs = [pscustomobject]@{
        aksName = @{ value = 'test-cluster' }
        userAssignedIdentityClientId = @{ value = 'gateway-identity' }
        workloadIdentityClientId = @{ value = 'workload-identity' }
        appInsightsConnectionString = @{ value = 'InstrumentationKey=test' }
        resourceLabel = @{ value = 'test' }
        tenantId = @{ value = 'test-tenant' }
        location = @{ value = 'westus2' }
        publicOrigin = @{ value = 'https://gateway.example/' }
        kubernetesNamespace = @{ value = $case.Namespace }
    }
    $failure = $null
    try {
        Deploy-KubernetesResources -Outputs $outputs -ResourceGroupName 'test-group' -ClientId 'test-client'
    }
    catch {
        $failure = $_
    }
    if ($case.Error) {
        Assert-True ($null -ne $failure -and $failure.Exception.Message.Contains($case.Error)) "Expected failure was not reported: $($case.Name)"
    }
    elseif ($failure) {
        throw $failure
    }
    else {
        Assert-True ($script:Commands.Count -eq 2) 'Expected one secret lookup and one apply.'
        Assert-True ($script:Manifest -notmatch '\$\{[A-Z_]+\}') 'The rendered manifest has unresolved placeholders.'
        Assert-True ($script:Manifest.Contains("Kubernetes__Namespace: `"$script:ExpectedNamespace`"")) 'The runtime namespace is inconsistent.'
        Assert-True ($script:Manifest -match "(?m)^  name: `"$script:ExpectedNamespace`"\r?$") 'The namespace resource name is inconsistent.'
        $namespaceFields = [regex]::Matches($script:Manifest, '(?m)^[ \t]+namespace: ([^\r\n]+)')
        Assert-True ($namespaceFields.Count -gt 0) 'The manifest is missing namespace fields.'
        foreach ($field in $namespaceFields) {
            Assert-True ($field.Groups[1].Value -ceq "`"$script:ExpectedNamespace`"") 'A resource or role binding used the wrong namespace or an unquoted scalar.'
        }
        $secretField = [regex]::Match($script:Manifest, '(?m)^  gatewaySecret: "([A-Za-z0-9+/=]+)"')
        Assert-True $secretField.Success 'The manifest must contain non-empty Base64 secret data.'
        $decoded = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($secretField.Groups[1].Value))
        if ($null -ne $case.Secret) {
            Assert-True ($decoded -ceq $case.Secret) 'The stored secret changed during redeployment.'
        }
        else {
            Assert-True ([Convert]::FromBase64String($decoded).Length -eq 32) 'The generated secret must contain 32 random bytes.'
        }
    }
    if ($script:ManifestPath) {
        Assert-True (-not (Test-Path -LiteralPath (Split-Path $script:ManifestPath -Parent))) 'Temporary secret-bearing files were not cleaned up.'
    }
    Write-Host "$($case.Name): passed"
}

$arm = Get-Content (Join-Path $deploymentRoot 'infra/azure-deployment.json') -Raw | ConvertFrom-Json -Depth 100
Assert-True ($arm.parameters.kubernetesNamespace.defaultValue -eq 'adapter') 'The default infrastructure namespace changed.'
Assert-True ($arm.outputs.kubernetesNamespace.value -eq "[parameters('kubernetesNamespace')]") 'Infrastructure must output the configured namespace.'
$federations = @($arm.resources | Where-Object type -like '*/federatedIdentityCredentials')
Assert-True ($federations.Count -eq 2) 'Expected gateway and workload identity federations.'
foreach ($federation in $federations) {
    Assert-True ($federation.properties.subject.Contains("parameters('kubernetesNamespace')")) 'Workload identity uses a hard-coded namespace.'
}
$embedded = $arm.resources | Where-Object type -eq 'Microsoft.Resources/deploymentScripts'
Assert-True (-not $embedded.properties.supportingScriptUris) 'The embedded deployment must not download an unrelated manifest.'
$clusterName = $embedded.properties.environmentVariables | Where-Object name -eq 'AKS_NAME'
Assert-True ($clusterName.value -eq $arm.outputs.aksName.value) 'The embedded deployment must use the actual AKS resource name.'
$secretVariable = $embedded.properties.environmentVariables | Where-Object name -eq 'GATEWAY_SECRET'
Assert-True ($secretVariable.secureValue -eq "[parameters('gatewaySecret')]") 'The embedded secret must remain a secure parameter.'

if ($BashPath) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new($BashPath)
    $startInfo.ArgumentList.Add('-n')
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    $process.StandardInput.Write($embedded.properties.scriptContent.Replace("`r`n", "`n"))
    $process.StandardInput.Close()
    $syntaxErrors = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    Assert-True ($process.ExitCode -eq 0) "Embedded Bash syntax validation failed: $syntaxErrors"
    $process.Dispose()
    Write-Host 'Embedded Bash syntax: passed'
}

Write-Host "$($cases.Count) offline deployment scenarios and ARM consistency checks passed. No Azure commands were executed."