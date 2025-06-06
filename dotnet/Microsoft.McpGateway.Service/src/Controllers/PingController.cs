// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Microsoft.McpGateway.Service.Controllers
{
    [ApiController]
    [Route("ping")]
    [AllowAnonymous]
    public class PingController : Controller
    {
        // GET /ping
        [HttpGet]
        public IActionResult Ping() => Ok();
    }
}
