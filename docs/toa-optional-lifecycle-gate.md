# Optional TOA verify for Kubernetes MCP lifecycle

MCP Gateway manages adapters and tools in Kubernetes with session-aware routing
and Entra ID roles. That answers deploy, route, and authorize. It does not prove
that a tool recently delivered a real result under an outside probe.

[TOA](https://github.com/Carmel-Labs-Inc/toa) (`toa/0.1`) is an Apache-2.0 signed
JSON evidence format for MCP tool delivery (reach, invoke, functional, shape,
and related layers). It is not a wire protocol. It is not meant to run on every
live `tools/call`.

## Suggested fit

Optional, off by default. Before `POST /adapters` or `POST /tools` promote into
production, require a recent attestation and verify offline:

```yaml
      - name: Verify tool delivery attestation
        if: hashFiles('toa.json') != ''
        run: |
          pip install "git+https://github.com/Carmel-Labs-Inc/toa.git@5a1bf1cf6a15a4864ea809fe7b2a073f2cef4e22#subdirectory=python"
          toa-verify toa.json --require-emitter agentstatus --require-layer functional=pass --max-age 7d
```

Example workflow: [`examples/toa-after-lifecycle.yml`](../examples/toa-after-lifecycle.yml).

## Out of scope

- Replacing Entra roles, adapter deploy, or the tool gateway router hot path
- Signing every production `tools/call`
