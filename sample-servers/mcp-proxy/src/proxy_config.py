from __future__ import annotations

import json
import re
from collections.abc import Callable, Mapping
from urllib.parse import urlparse

import httpx
from azure.identity import WorkloadIdentityCredential
from azure.keyvault.secrets import SecretClient
from fastmcp.client.transports import StreamableHttpTransport

MAX_HEADERS = 16
MAX_HEADER_NAME_LEN = 128
MAX_HEADER_VALUE_LEN = 8192
MAX_HEADERS_JSON_LEN = 65536
HEADER_NAME_PATTERN = re.compile(r"[!#$%&'*+\-.^_`|~0-9A-Za-z]+")
HEADER_VALUE_PATTERN = re.compile(r"[\x20-\x7e]+")
KEY_VAULT_HOST_PATTERN = re.compile(r"[a-z0-9-]+\.vault\.azure\.net")
KEY_VAULT_SECRET_NAME_PATTERN = re.compile(r"[0-9A-Za-z-]+")
KEY_VAULT_SECRET_VERSION_PATTERN = re.compile(r"[0-9A-Fa-f]{32}")
FORBIDDEN_PROXY_HEADERS = {
    "connection",
    "content-length",
    "host",
    "keep-alive",
    "proxy-authenticate",
    "proxy-authorization",
    "te",
    "trailer",
    "transfer-encoding",
    "upgrade",
}

SecretLoader = Callable[[str], str]


class ProxyConfigurationError(ValueError):
    """Raised when remote proxy configuration is unsafe or invalid."""


class _JsonObject(list[tuple[str, object]]):
    """Distinguish JSON objects from arrays while preserving duplicate keys."""


def valid_proxy_url(url: str) -> bool:
    if not isinstance(url, str) or any(character.isspace() for character in url):
        return False

    try:
        parsed = urlparse(url)
        hostname = parsed.hostname
        port = parsed.port
    except ValueError:
        return False

    return (
        parsed.scheme in {"http", "https"}
        and hostname is not None
        and parsed.username is None
        and parsed.password is None
        and (port is None or 0 < port <= 65535)
        and not parsed.fragment
    )


def parse_proxy_headers(raw_headers: str) -> dict[str, str]:
    if (
        not isinstance(raw_headers, str)
        or not raw_headers
        or len(raw_headers) > MAX_HEADERS_JSON_LEN
    ):
        raise ProxyConfigurationError(
            "Invalid proxy header secret: expected a non-empty JSON object"
        )

    try:
        parsed = json.loads(raw_headers, object_pairs_hook=_JsonObject)
    except json.JSONDecodeError as error:
        raise ProxyConfigurationError(
            "Invalid proxy header secret: expected a JSON object"
        ) from error

    if not isinstance(parsed, _JsonObject):
        raise ProxyConfigurationError(
            "Invalid proxy header secret: expected a JSON object"
        )
    if len(parsed) > MAX_HEADERS:
        raise ProxyConfigurationError("Too many MCP proxy headers")

    headers: dict[str, str] = {}
    normalized_names: set[str] = set()
    for name, value in parsed:
        if (
            not isinstance(name, str)
            or not name
            or len(name) > MAX_HEADER_NAME_LEN
            or HEADER_NAME_PATTERN.fullmatch(name) is None
            or name.lower() in FORBIDDEN_PROXY_HEADERS
        ):
            raise ProxyConfigurationError("Unsafe MCP proxy header name")

        normalized_name = name.lower()
        if normalized_name in normalized_names:
            raise ProxyConfigurationError("Duplicate MCP proxy header name")
        normalized_names.add(normalized_name)

        if (
            not isinstance(value, str)
            or len(value) > MAX_HEADER_VALUE_LEN
            or HEADER_VALUE_PATTERN.fullmatch(value) is None
        ):
            raise ProxyConfigurationError(f"Unsafe MCP proxy header value for {name!r}")
        headers[name] = value

    return headers


def parse_key_vault_secret_url(secret_url: str) -> tuple[str, str, str | None]:
    try:
        parsed = urlparse(secret_url)
        hostname = parsed.hostname
        port = parsed.port
    except ValueError as error:
        raise ProxyConfigurationError("Invalid Key Vault secret URL") from error

    if (
        parsed.scheme != "https"
        or hostname is None
        or KEY_VAULT_HOST_PATTERN.fullmatch(hostname) is None
        or parsed.username is not None
        or parsed.password is not None
        or port is not None
        or parsed.query
        or parsed.fragment
    ):
        raise ProxyConfigurationError("Invalid Key Vault secret URL")

    path_parts = parsed.path.removeprefix("/").split("/")
    if len(path_parts) not in {2, 3} or path_parts[0] != "secrets":
        raise ProxyConfigurationError("Invalid Key Vault secret URL")

    secret_name = path_parts[1]
    secret_version = path_parts[2] if len(path_parts) == 3 else None
    if KEY_VAULT_SECRET_NAME_PATTERN.fullmatch(secret_name) is None:
        raise ProxyConfigurationError("Invalid Key Vault secret URL")
    if (
        secret_version is not None
        and KEY_VAULT_SECRET_VERSION_PATTERN.fullmatch(secret_version) is None
    ):
        raise ProxyConfigurationError("Invalid Key Vault secret URL")

    return f"https://{hostname}", secret_name, secret_version


def load_key_vault_secret(secret_url: str) -> str:
    vault_url, secret_name, secret_version = parse_key_vault_secret_url(secret_url)
    credential = WorkloadIdentityCredential()
    client = SecretClient(vault_url=vault_url, credential=credential)
    try:
        value = client.get_secret(secret_name, secret_version).value
    finally:
        client.close()
        credential.close()

    if value is None:
        raise ProxyConfigurationError("Key Vault proxy header secret is empty")
    return value


def create_no_redirect_http_client(
    *,
    headers: dict[str, str] | None = None,
    auth: httpx.Auth | None = None,
    timeout: httpx.Timeout | None = None,
    follow_redirects: bool = False,
    transport: httpx.AsyncBaseTransport | None = None,
) -> httpx.AsyncClient:
    del follow_redirects
    return httpx.AsyncClient(
        headers=headers,
        auth=auth,
        timeout=timeout or httpx.Timeout(30.0, read=300.0),
        follow_redirects=False,
        transport=transport,
    )


def create_remote_transport(
    proxy_url: str,
    headers_secret_url: str | None,
    *,
    secret_loader: SecretLoader = load_key_vault_secret,
) -> StreamableHttpTransport:
    if not valid_proxy_url(proxy_url):
        raise ProxyConfigurationError(
            "Invalid MCP_PROXY_URL: expected a well-formed HTTP(S) URL"
        )

    headers: dict[str, str] = {}
    if headers_secret_url:
        if urlparse(proxy_url).scheme != "https":
            raise ProxyConfigurationError(
                "MCP_PROXY_URL must use HTTPS when proxy headers are configured"
            )
        parse_key_vault_secret_url(headers_secret_url)
        try:
            raw_headers = secret_loader(headers_secret_url)
        except ProxyConfigurationError:
            raise
        except Exception as error:
            raise ProxyConfigurationError(
                "Unable to load MCP proxy headers from Key Vault"
            ) from error
        headers = parse_proxy_headers(raw_headers)

    return StreamableHttpTransport(
        proxy_url,
        headers=headers,
        httpx_client_factory=create_no_redirect_http_client,
    )


def remote_transport_from_environment(
    environ: Mapping[str, str],
    *,
    secret_loader: SecretLoader = load_key_vault_secret,
) -> StreamableHttpTransport:
    if environ.get("MCP_PROXY_HEADERS"):
        raise ProxyConfigurationError(
            "MCP_PROXY_HEADERS is unsafe. Store headers in Key Vault and set "
            "MCP_PROXY_HEADERS_SECRET_URL instead"
        )

    proxy_url = environ.get("MCP_PROXY_URL")
    if not proxy_url:
        raise ProxyConfigurationError("MCP_PROXY_URL is required")

    return create_remote_transport(
        proxy_url,
        environ.get("MCP_PROXY_HEADERS_SECRET_URL"),
        secret_loader=secret_loader,
    )
