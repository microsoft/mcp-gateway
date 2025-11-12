from fastmcp import FastMCP

# Create server
app = FastMCP("MCP Example Server")


@app.tool()
def add(a: int, b: int) -> int:
    """Add two numbers"""
    print(f"[debug-server] add({a}, {b})")
    return a + b
