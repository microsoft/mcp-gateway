from contextlib import asynccontextmanager
import json
import os

import anyio
from mcp import types
from mcp.server.lowlevel import Server
from mcp.server.streamable_http_manager import StreamableHTTPSessionManager
from starlette.applications import Starlette
from starlette.routing import Route

state = {"active": 0, "started": 0, "cancelled": 0}


async def list_tools(context, params):
    return types.ListToolsResult(
        tools=[types.Tool(name="stream_state", inputSchema={"type": "object"})],
        ttlMs=0,
        cacheScope="private",
    )


async def call_tool(context, params):
    return types.CallToolResult(content=[types.TextContent(type="text", text=json.dumps(state))])


async def listen(context, params):
    state["active"] += 1
    state["started"] += 1
    metadata = {"io.modelcontextprotocol/subscriptionId": context.request_id}
    try:
        await context.session.send_notification(
            types.SubscriptionsAcknowledgedNotification(
                params=types.SubscriptionsAcknowledgedNotificationParams(
                    notifications=types.SubscriptionFilter(toolsListChanged=bool(params.notifications.tools_list_changed)),
                    _meta=metadata,
                )
            ),
            related_request_id=context.request_id,
        )
        await anyio.sleep(float(os.environ.get("E2E_QUIET_SECONDS", "30")))
        if params.notifications.tools_list_changed:
            await context.session.send_notification(
                types.ToolListChangedNotification(params=types.NotificationParams(_meta=metadata)),
                related_request_id=context.request_id,
            )
        await anyio.sleep_forever()
    finally:
        state["active"] -= 1
        state["cancelled"] += 1


class StreamingFixture(Server):
    def get_capabilities(self, *args, **kwargs):
        capabilities = super().get_capabilities(*args, **kwargs)
        capabilities.tools.listChanged = True
        return capabilities


server = StreamingFixture(
    "MCP E2E Streaming Fixture",
    version="1.0.0",
    on_list_tools=list_tools,
    on_call_tool=call_tool,
    on_subscriptions_listen=listen,
)
manager = StreamableHTTPSessionManager(server, stateless=True)


class McpEndpoint:
    async def __call__(self, scope, receive, send):
        await manager.handle_request(scope, receive, send)


@asynccontextmanager
async def lifespan(app):
    async with manager.run():
        yield


app = Starlette(routes=[Route("/mcp", endpoint=McpEndpoint(), methods=["POST"])], lifespan=lifespan)