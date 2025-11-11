// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Service;

namespace Microsoft.McpGateway.Service.Controllers
{
    /// <summary>
    /// Controller for managing tool deployments.
    /// Tools are deployed like adapters but include additional tool definition metadata.
    /// </summary>
    [ApiController]
    [Route("tools")]
    [Authorize]
    public class ToolManagementController(IToolManagementService managementService, IAdapterRichResultProvider adapterRichResultProvider) : ControllerBase
    {
        private readonly IToolManagementService _managementService = managementService ?? throw new ArgumentNullException(nameof(managementService));
        private readonly IAdapterRichResultProvider _adapterRichResultProvider = adapterRichResultProvider ?? throw new ArgumentNullException(nameof(adapterRichResultProvider));

        // POST /tools
        [HttpPost]
        public async Task<IActionResult> CreateTool([FromBody] ToolData request, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _managementService.CreateAsync(HttpContext.User, request, cancellationToken).ConfigureAwait(false);
                return CreatedAtAction(nameof(GetTool), new { name = result.Name }, result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        // GET /tools/{name}
        [HttpGet("{name}")]
        public async Task<IActionResult> GetTool(string name, CancellationToken cancellationToken)
        {
            try
            {
                var tool = await _managementService.GetAsync(HttpContext.User, name, cancellationToken).ConfigureAwait(false);
                if (tool == null)
                    return NotFound();
                else
                    return Ok(tool);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }

        // GET /tools/{name}/status
        [HttpGet("{name}/status")]
        public async Task<IActionResult> GetToolStatus(string name, CancellationToken cancellationToken)
        {
            try
            {
                var tool = await _managementService.GetAsync(HttpContext.User, name, cancellationToken).ConfigureAwait(false);
                if (tool == null)
                    return NotFound();
                else
                    return Ok(await _adapterRichResultProvider.GetAdapterStatusAsync(tool.Name, cancellationToken).ConfigureAwait(false));
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }

        // GET /tools/{name}/logs?instance=0
        [HttpGet("{name}/logs")]
        public async Task<IActionResult> GetToolLogs(string name, [FromQuery] int instance = 0, CancellationToken cancellationToken = default)
        {
            try
            {
                var tool = await _managementService.GetAsync(HttpContext.User, name, cancellationToken).ConfigureAwait(false);
                if (tool == null)
                    return NotFound();
                else
                    return Ok(await _adapterRichResultProvider.GetAdapterLogsAsync(tool.Name, instance, cancellationToken).ConfigureAwait(false));
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }

        // PUT /tools/{name}
        [HttpPut("{name}")]
        public async Task<IActionResult> UpdateTool(string name, [FromBody] ToolData request, CancellationToken cancellationToken)
        {
            if (!string.Equals(name, request.Name, StringComparison.OrdinalIgnoreCase))
                return BadRequest("Tool name in URL and body must match.");

            try
            {
                var tool = await _managementService.UpdateAsync(HttpContext.User, request, cancellationToken).ConfigureAwait(false);
                return Ok(tool);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }

        // DELETE /tools/{name}
        [HttpDelete("{name}")]
        public async Task<IActionResult> DeleteTool(string name, CancellationToken cancellationToken)
        {
            try
            {
                await _managementService.DeleteAsync(HttpContext.User, name, cancellationToken).ConfigureAwait(false);
                return NoContent();
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }

        // GET /tools
        [HttpGet]
        public async Task<IActionResult> ListTools(CancellationToken cancellationToken)
        {
            var tools = await _managementService.ListAsync(HttpContext.User, cancellationToken).ConfigureAwait(false);
            return Ok(tools);
        }
    }
}
