import asyncio
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

from mcp import types
import main


class SubscriptionTests(unittest.IsolatedAsyncioTestCase):
    async def test_acknowledges_and_releases_on_cancellation(self):
        session = SimpleNamespace(send_notification=AsyncMock())
        context = SimpleNamespace(session=session, request_id=123)
        params = types.SubscriptionsListenRequestParams(
            notifications=types.SubscriptionFilter(toolsListChanged=True)
        )
        with patch("main.anyio.sleep", side_effect=asyncio.CancelledError):
            with self.assertRaises(asyncio.CancelledError):
                await main.listen(context, params)
        notification = session.send_notification.call_args.args[0]
        self.assertEqual(notification.method, "notifications/subscriptions/acknowledged")
        self.assertTrue(notification.params.notifications.tools_list_changed)
        self.assertEqual(main.state["active"], 0)
        self.assertGreater(main.state["cancelled"], 0)


if __name__ == "__main__":
    unittest.main()