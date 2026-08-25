import os
import shlex
import sys
from typing import NoReturn

from fastmcp.server import create_proxy
from proxy_config import ProxyConfigurationError, remote_transport_from_environment

MAX_ARGS = 64
MAX_ARG_LEN = 512


def bad(msg: str) -> NoReturn:
    print(f"[shim] {msg}", file=sys.stderr)
    sys.exit(1)


def safe(arg: str) -> bool:
    return (
        isinstance(arg, str)
        and 0 < len(arg) <= MAX_ARG_LEN
        and "\x00" not in arg
        and "\n" not in arg
        and "\r" not in arg
    )


proxy_url = os.environ.get("MCP_PROXY_URL")
if proxy_url:
    try:
        transport = remote_transport_from_environment(os.environ)
    except ProxyConfigurationError as error:
        bad(str(error))
    app = create_proxy(transport, name="MCP Proxy Server")
else:
    cmd = os.environ.get("MCP_COMMAND")
    if not cmd:
        bad("Must set either MCP_PROXY_URL or MCP_COMMAND environment variable")
    raw_args = os.environ.get("MCP_ARGS", "")
    args = shlex.split(raw_args) if raw_args else []

    if not safe(cmd):
        bad("Unsafe command")
    if len(args) > MAX_ARGS:
        bad("Too many args")
    for a in args:
        if not safe(a):
            bad(f"Unsafe arg: {a!r}")

    config: dict[str, object] = {
        "mcpServers": {
            "default": {
                "type": "stdio",
                "command": cmd,
                "args": args,
                "env": dict(os.environ),
            }
        }
    }
    app = create_proxy(config, name="MCP Proxy Server")

if __name__ == "__main__":
    app.settings.host = "127.0.0.1"
    app.settings.port = 8000
    app.run(transport="streamable-http")
