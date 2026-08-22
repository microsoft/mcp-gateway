import asyncio
import os
import subprocess
import sys
from pathlib import Path

import httpx
import pytest

SOURCE_DIR = Path(__file__).resolve().parents[1] / "src"
sys.path.insert(0, str(SOURCE_DIR))

from proxy_config import (
    ProxyConfigurationError,
    create_no_redirect_http_client,
    parse_key_vault_secret_url,
    parse_proxy_headers,
    remote_transport_from_environment,
    valid_proxy_url,
)

SECRET_URL = "https://example.vault.azure.net/secrets/mcp-proxy-headers"


@pytest.mark.parametrize(
    "proxy_url",
    [
        "https://internal-mcp-server/mcp",
        "http://localhost:8000/mcp",
        "http://mcp-service.adapter.svc.cluster.local/mcp",
    ],
)
def test_valid_proxy_url_accepts_internal_servers(proxy_url: str) -> None:
    assert valid_proxy_url(proxy_url)


def test_parse_proxy_headers_accepts_visible_ascii_values() -> None:
    headers = parse_proxy_headers(
        '{"Authorization":"Bearer test-token","X-Tenant":"tenant 1"}'
    )

    assert headers == {
        "Authorization": "Bearer test-token",
        "X-Tenant": "tenant 1",
    }


@pytest.mark.parametrize("raw_headers", ["not-json", "[]", '"header"'])
def test_parse_proxy_headers_rejects_malformed_json(raw_headers: str) -> None:
    with pytest.raises(ProxyConfigurationError, match="expected a JSON object"):
        parse_proxy_headers(raw_headers)


@pytest.mark.parametrize(
    "raw_headers",
    [
        '{"X-Test":"café"}',
        '{"X-Test":"line\\nbreak"}',
        '{"X-Test":"\\u0000"}',
        '{"X-Test":12}',
    ],
)
def test_parse_proxy_headers_rejects_invalid_values(raw_headers: str) -> None:
    with pytest.raises(ProxyConfigurationError, match="Unsafe MCP proxy header value"):
        parse_proxy_headers(raw_headers)


def test_parse_proxy_headers_rejects_case_insensitive_duplicates() -> None:
    with pytest.raises(ProxyConfigurationError, match="Duplicate MCP proxy header"):
        parse_proxy_headers('{"Authorization":"first","authorization":"second"}')


def test_parse_key_vault_secret_url_accepts_a_secret_reference() -> None:
    assert parse_key_vault_secret_url(SECRET_URL) == (
        "https://example.vault.azure.net",
        "mcp-proxy-headers",
        None,
    )


@pytest.mark.parametrize(
    "secret_url",
    [
        "http://example.vault.azure.net/secrets/headers",
        "https://[invalid]/secrets/headers",
        "https://attacker.example/secrets/headers",
        "https://example.vault.azure.net//secrets/headers",
        "https://example.vault.azure.net/secrets//headers",
        "https://example.vault.azure.net/keys/headers",
        "https://example.vault.azure.net/secrets/headers/not-a-version",
    ],
)
def test_parse_key_vault_secret_url_rejects_untrusted_urls(
    secret_url: str,
) -> None:
    with pytest.raises(ProxyConfigurationError, match="Invalid Key Vault secret URL"):
        parse_key_vault_secret_url(secret_url)


def test_raw_header_environment_variable_is_rejected() -> None:
    with pytest.raises(ProxyConfigurationError, match="MCP_PROXY_HEADERS is unsafe"):
        remote_transport_from_environment(
            {
                "MCP_PROXY_URL": "https://upstream.example/mcp",
                "MCP_PROXY_HEADERS": '{"Authorization":"Bearer exposed"}',
            }
        )


def test_secret_reference_loads_headers_at_runtime() -> None:
    loaded_urls: list[str] = []

    def load_secret(secret_url: str) -> str:
        loaded_urls.append(secret_url)
        return '{"Authorization":"Bearer runtime-only"}'

    transport = remote_transport_from_environment(
        {
            "MCP_PROXY_URL": "https://upstream.example/mcp",
            "MCP_PROXY_HEADERS_SECRET_URL": SECRET_URL,
        },
        secret_loader=load_secret,
    )

    assert loaded_urls == [SECRET_URL]
    assert transport.headers == {"Authorization": "Bearer runtime-only"}


def test_secret_backed_headers_require_https() -> None:
    with pytest.raises(ProxyConfigurationError, match="must use HTTPS"):
        remote_transport_from_environment(
            {
                "MCP_PROXY_URL": "http://upstream.example/mcp",
                "MCP_PROXY_HEADERS_SECRET_URL": SECRET_URL,
            },
            secret_loader=lambda _url: '{"Authorization":"Bearer secret"}',
        )


def test_http_proxy_without_static_headers_remains_supported() -> None:
    transport = remote_transport_from_environment(
        {"MCP_PROXY_URL": "http://mcp-service.adapter.svc.cluster.local/mcp"}
    )

    assert str(transport.url) == "http://mcp-service.adapter.svc.cluster.local/mcp"
    assert transport.headers == {}


def test_http_client_rejects_redirects_with_static_headers() -> None:
    sentinel = "redirect-sentinel"
    requests: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        requests.append(request)
        return httpx.Response(
            302,
            headers={"Location": "https://attacker.example/collect"},
        )

    async def exercise() -> httpx.Response:
        client = create_no_redirect_http_client(
            headers={"X-Api-Key": sentinel},
            follow_redirects=True,
            transport=httpx.MockTransport(handler),
        )
        async with client:
            return await client.get("https://upstream.example/mcp")

    response = asyncio.run(exercise())

    assert response.status_code == 302
    assert len(requests) == 1
    assert requests[0].url == httpx.URL("https://upstream.example/mcp")


def test_fastmcp_debug_logs_redact_headers() -> None:
    sentinel = "logging-sentinel"
    script = """
import sys

sys.path.insert(0, sys.argv[1])

from fastmcp.server import create_proxy
from fastmcp.utilities.logging import get_logger
from proxy_config import create_remote_transport

sentinel = sys.argv[2]
transport = create_remote_transport(
    "https://upstream.example/mcp",
    "https://example.vault.azure.net/secrets/mcp-proxy-headers",
    secret_loader=lambda _url: '{"Authorization":"Bearer ' + sentinel + '"}',
)
create_proxy(transport, name="Test Proxy")
get_logger("proxy_security_test").debug("Transport: %r", transport)
"""

    completed = subprocess.run(
        [sys.executable, "-c", script, str(SOURCE_DIR), sentinel],
        check=True,
        capture_output=True,
        text=True,
        env={**os.environ, "FASTMCP_LOG_LEVEL": "DEBUG"},
    )
    output = completed.stdout + completed.stderr

    assert "StreamableHttpTransport" in output
    assert sentinel not in output
