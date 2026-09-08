# End-to-End Tests

These tests exercise a running gateway, the Python examples, and registered HTTP tools. They cover modern discovery/direct calls, protocol errors, authorization, private catalogs, and incremental streaming with cancellation. For upgrade requirements and request examples, see [MCP 2026-07-28 migration](../../docs/mcp-2026-07-28.md).

Run the commands below from the repository root. Prerequisites are .NET 8 SDK or later, Node 22.18 or later, PowerShell 7.4 or later for the examples, Docker Desktop with Kubernetes, and kubectl. Install portal dependencies with `npm --prefix portal ci`.

## Local E2E

The [local overlay](local/kustomization.yaml) uses namespace `mcp-modern-e2e`, a shared Redis store, and two gateway and two Tools replicas. It does not replace a deployment in the default `adapter` namespace. Always specify the Kubernetes context.

1. Run the unit tests and portal build:

  ```powershell
  dotnet test dotnet/Microsoft.McpGateway.sln --configuration Debug
  npm --prefix portal test
  npm --prefix portal run build
  ```

2. Start a local registry if port 5000 is unused:

  ```powershell
  docker run -d --name mcp-modern-e2e-registry -p 127.0.0.1:5000:5000 registry:2.8
  ```

3. Build and push the fixtures. The overlay and test harness default to tag `e2e`. For subsequent runs, choose a new tag, update both image entries in the overlay, and set `MCP_IMAGE_TAG` to the fixture tag so a stale local image cannot mask a change.

  ```powershell
  $tag = 'e2e'
  $fixtures = @{
     'mcp-example' = 'sample-servers/mcp-example'
     'mcp-proxy' = 'sample-servers/mcp-proxy'
     'weather-tool' = 'sample-servers/tool-example'
     'mcp-stream-fixture' = 'deployment/e2e/stream-server'
  }
  foreach ($fixture in $fixtures.GetEnumerator()) {
     docker build -f "$($fixture.Value)/Dockerfile" $fixture.Value -t "localhost:5000/$($fixture.Key):$tag"
     if ($LASTEXITCODE -ne 0) { throw "Build failed: $($fixture.Key)" }
     docker push "localhost:5000/$($fixture.Key):$tag"
     if ($LASTEXITCODE -ne 0) { throw "Push failed: $($fixture.Key)" }
  }
  ```

4. Publish both gateway images with the same tag as the overlay. Use fresh publish directories to avoid stale output from earlier SDK versions. Pull the published images into Docker Desktop's image store before Kubernetes uses them.

  ```powershell
  $publishRoot = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
  dotnet publish dotnet/Microsoft.McpGateway.Service/src/Microsoft.McpGateway.Service.csproj -c Release /p:PublishProfile=localhost_5000.pubxml /p:PublishImageTag=$tag /p:PublishDir="$publishRoot/service/"
  dotnet publish dotnet/Microsoft.McpGateway.Tools/src/Microsoft.McpGateway.Tools.csproj -c Release /p:PublishProfile=localhost_5000.pubxml /p:PublishImageTag=$tag /p:PublishDir="$publishRoot/tools/"
  docker pull "localhost:5000/microsoft-mcpgateway-service:$tag"
  docker pull "localhost:5000/microsoft-mcpgateway-tools:$tag"
  ```

5. Validate and apply the overlay, then wait for its deployments:

  ```powershell
  kubectl --context docker-desktop apply --dry-run=client -k deployment/e2e/local
  kubectl --context docker-desktop apply -k deployment/e2e/local
  kubectl --context docker-desktop -n mcp-modern-e2e rollout status deployment/redis --timeout=180s
  kubectl --context docker-desktop -n mcp-modern-e2e rollout status deployment/mcpgateway --timeout=180s
  kubectl --context docker-desktop -n mcp-modern-e2e rollout status statefulset/toolgateway --timeout=180s
  ```

6. Start a port-forward in a separate terminal:

  ```powershell
  kubectl --context docker-desktop -n mcp-modern-e2e port-forward --address 127.0.0.1 service/mcpgateway-service 18000:8000
  ```

7. Register and wait for the test fixtures, then run verification:

  ```powershell
  $env:MCP_IMAGE_TAG = $tag
  node deployment/e2e/test-mcp.mjs setup
  foreach ($name in @('e2e-example', 'e2e-proxy', 'e2e-stream', 'e2e-weather')) {
     kubectl --context docker-desktop -n mcp-modern-e2e rollout status "statefulset/$name" --timeout=240s
  }
  node deployment/e2e/test-mcp.mjs verify
  ```

8. Open `http://localhost:18000/portal/`. Use development identity `e2e-owner` with role `mcp.admin` to inspect the fixtures, connect to an adapter, and run a tool. Synthetic development identities are for local tests only.

The test-only network policy permits just `e2e-proxy` to call `e2e-example` on port 8000. Record running pod `imageID` values when comparing builds. To clean up, delete only the dedicated namespace and registry after confirming they contain no work you need:

```powershell
kubectl --context docker-desktop delete namespace mcp-modern-e2e
docker rm -f mcp-modern-e2e-registry
```

## Cloud E2E

Use a dedicated test subscription/resource group or another explicitly isolated environment. Deploy using the [Azure guide](../infra/README.md) with exact gateway and fixture image tags. Validate regional quota, permissions, policy, cost, and TLS before running the tests.

1. Configure a single-tenant Entra API app using the [authentication instructions](../../README.md#2-setup-entra-id-azure-active-directory). Use your own subscription, tenant, client ID, resource label, and supported region.
2. Deploy infrastructure and publish all images to ACR before the Kubernetes stage. Verify kubelet `AcrPull` and the gateway's Cosmos data-plane role. If policy blocks public Cosmos access, enable its private endpoint rather than opening the firewall.
3. Deploy the checked-out manifest with `-GatewayImage` and `-ToolGatewayImage`. Keep cloud Entra authentication enabled. Use two gateway and Tools replicas for the replica-independent routing test when the cluster has sufficient capacity.
4. Set the harness environment using your HTTPS endpoint and an API token obtained in the current process:

  ```powershell
  $env:MCP_BASE_URL = 'https://<gateway-hostname>/'
  $env:MCP_NAMESPACE = 'adapter'
  $env:MCP_IMAGE_TAG = '<fixture-image-tag>'
  $token = az account get-access-token --tenant '<tenant-id>' --scope 'api://<client-id>/access' --output json | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) { throw 'Token acquisition failed.' }
  $env:MCP_E2E_TOKEN = $token.accessToken
  ```

  Never print or commit tokens. For a short-lived test certificate, set `NODE_EXTRA_CA_CERTS` to its public PEM certificate; do not disable TLS verification. Production deployments need a trusted certificate.

5. Run `node deployment/e2e/test-mcp.mjs setup`, apply [the fixture-only network policy](local/proxy-network-policy.yaml) using the cloud test context and namespace, and wait for the fixture rollouts. Then run `node deployment/e2e/test-mcp.mjs verify` against the public endpoint. Cloud verification includes unauthenticated `401`; synthetic cross-caller checks run only locally.
6. Clear `MCP_E2E_TOKEN` when finished and remove only the dedicated test resources and app registration. Stopping AKS does not stop Application Gateway, registry, or storage charges.

Application Gateway can consume the `X-Accel-Buffering` hint. The local suite checks that the gateway supplies it; cloud verification checks prompt event delivery, a 30-second quiet interval, and downstream cancellation through the public ingress.

## Harness Settings

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `MCP_BASE_URL` | `http://localhost:18000` | Gateway endpoint |
| `MCP_NAMESPACE` | `mcp-modern-e2e` | Namespace of the test fixture backends |
| `MCP_IMAGE_TAG` | `e2e` | Tag used to register fixture images |
| `MCP_E2E_TOKEN` | Unset | Entra bearer token for HTTPS cloud tests; not logged |
| `NODE_EXTRA_CA_CERTS` | Unset | Optional public certificate trust for an isolated TLS test |

Setup can be repeated, but it refuses to silently use a fixture with a different image tag. Use a separate namespace/deployment when changing fixture versions.

## Streaming Fixture

The basic FastMCP example does not implement `subscriptions/listen`. The [test fixture](stream-server/main.py) uses the official Python SDK to acknowledge a subscription immediately, send an event after a quiet interval, and report cancellation counters. It is not a production gateway feature.

To test the handler directly, run the fixture container with its source mounted read-only at `/app` and execute `python -m unittest test_main -v`.