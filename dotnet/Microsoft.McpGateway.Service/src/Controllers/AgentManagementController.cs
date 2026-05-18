// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Service;

namespace Microsoft.McpGateway.Service.Controllers
{
    /// <summary>
    /// Controller for managing agent definitions. Agents are metadata-only
    /// (system prompt + model + tool list); they are not deployed as pods.
    /// </summary>
    [ApiController]
    [Route("agents")]
    [Authorize]
    public class AgentManagementController(IAgentManagementService managementService) : ControllerBase
    {
        private readonly IAgentManagementService _managementService = managementService ?? throw new ArgumentNullException(nameof(managementService));

        // POST /agents
        [HttpPost]
        public async Task<IActionResult> CreateAgent([FromBody] AgentData request, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _managementService.CreateAsync(HttpContext.User, request, cancellationToken).ConfigureAwait(false);
                return CreatedAtAction(nameof(GetAgent), new { name = result.Name }, result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        // GET /agents/{name}
        [HttpGet("{name}")]
        public async Task<IActionResult> GetAgent(string name, CancellationToken cancellationToken)
        {
            try
            {
                var agent = await _managementService.GetAsync(HttpContext.User, name, cancellationToken).ConfigureAwait(false);
                if (agent == null)
                    return NotFound();
                return Ok(agent);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }

        // PUT /agents/{name}
        [HttpPut("{name}")]
        public async Task<IActionResult> UpdateAgent(string name, [FromBody] AgentData request, CancellationToken cancellationToken)
        {
            if (!string.Equals(name, request.Name, StringComparison.OrdinalIgnoreCase))
                return BadRequest("Agent name in URL and body must match.");

            try
            {
                var agent = await _managementService.UpdateAsync(HttpContext.User, request, cancellationToken).ConfigureAwait(false);
                return Ok(agent);
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

        // DELETE /agents/{name}
        [HttpDelete("{name}")]
        public async Task<IActionResult> DeleteAgent(string name, CancellationToken cancellationToken)
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

        // GET /agents
        [HttpGet]
        public async Task<IActionResult> ListAgents(CancellationToken cancellationToken)
        {
            var agents = await _managementService.ListAsync(HttpContext.User, cancellationToken).ConfigureAwait(false);
            return Ok(agents);
        }
    }
}
