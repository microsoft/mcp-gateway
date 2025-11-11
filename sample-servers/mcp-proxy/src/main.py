from fastmcp import FastMCP
import os, sys, shlex
import validators

MAX_ARGS = 64
MAX_ARG_LEN = 512

def bad(msg: str):
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
    if not validators.url(proxy_url):
        bad("Invalid MCP_PROXY_URL: must be http(s) and a well-formed URL")
    config = {
        "mcpServers": {
            "default": {
                "url": proxy_url,
                "transport": "http"
            }
        }
    }
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

    config = {
        "mcpServers": {
            "default": {
                "type": "stdio",
                "command": cmd,
                "args": args,
                "env": dict(os.environ)
            }
        }
    }

app = FastMCP.as_proxy(config, name="MCP Proxy Server")

if __name__ == "__main__":
    app.settings.host = "127.0.0.1"
    app.settings.port = 8000
    app.run(transport="streamable-http")
