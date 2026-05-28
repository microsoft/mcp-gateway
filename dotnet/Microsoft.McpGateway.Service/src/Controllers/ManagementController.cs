// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using k8s.Autorest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Service;

namespace Microsoft.McpGateway.Service.Controllers
{
    [ApiController]
    [Route("adapters")]
    [Authorize]
    public class ManagementController(IAdapterManagementService managementService, IAdapterRichResultProvider adapterRichResultProvider) : ControllerBase
    {
        private readonly IAdapterManagementService _managementService = managementService ?? throw new ArgumentNullException(nameof(managementService));
        private readonly IAdapterRichResultProvider _adapterRichResultProvider = adapterRichResultProvider ?? throw new ArgumentNullException(nameof(adapterRichResultProvider));

        // POST /adapters
        [HttpPost]
        public async Task<IActionResult> CreateAdapter([FromBody] AdapterData request, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _managementService.CreateAsync(HttpContext.User, request, cancellationToken).ConfigureAwait(false);
                return CreatedAtAction(nameof(GetAdapter), new { name = result.Name }, result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        // GET /adapters/{name}
        [HttpGet("{name}")]
        public async Task<IActionResult> GetAdapter(string name, CancellationToken cancellationToken)
        {
            try
            {
                var adapter = await _managementService.GetAsync(HttpContext.User, name, cancellationToken).ConfigureAwait(false);
                if (adapter == null)
                    return NotFound();
                else
                    return Ok(adapter);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }

        // GET /adapters/{name}/status
        [HttpGet("{name}/status")]
        public async Task<IActionResult> GetAdapterStatus(string name, CancellationToken cancellationToken)
        {
            try
            {
                var adapter = await _managementService.GetAsync(HttpContext.User, name, cancellationToken).ConfigureAwait(false);
                if (adapter == null)
                    return NotFound();
                else
                    return Ok(await _adapterRichResultProvider.GetAdapterStatusAsync(adapter.Name, cancellationToken).ConfigureAwait(false));
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }

        // GET /adapters/{name}/logs?instance=0
        [HttpGet("{name}/logs")]
        public async Task<IActionResult> GetAdapterLogs(string name, [FromQuery] int instance = 0, CancellationToken cancellationToken = default)
        {
            try
            {
                var adapter = await _managementService.GetAsync(HttpContext.User, name, cancellationToken).ConfigureAwait(false);
                if (adapter == null)
                    return NotFound();
                else
                    return Ok(await _adapterRichResultProvider.GetAdapterLogsAsync(adapter.Name, instance, cancellationToken).ConfigureAwait(false));
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            // The k8s API returns 400 BadRequest while the pod is still ContainerCreating
            // or before the container has produced any output. Surface that as a 200 with
            // a placeholder string so the portal log panel can render gracefully instead
            // of bubbling a 500 up to the user immediately after creating an adapter.
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                return Ok($"Logs are not yet available for instance {instance} (pod is starting). Try again in a few seconds.");
            }
        }

        // PUT /adapters/{name}
        [HttpPut("{name}")]
        public async Task<IActionResult> UpdateAdapter(string name, [FromBody] AdapterData request, CancellationToken cancellationToken)
        {
            if (!string.Equals(name, request.Name, StringComparison.OrdinalIgnoreCase))
                return BadRequest("Adapter name in URL and body must match.");

            try
            {
                var adapter = await _managementService.UpdateAsync(HttpContext.User, request, cancellationToken).ConfigureAwait(false);
                return Ok(adapter);
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

        // DELETE /adapters/{name}
        [HttpDelete("{name}")]
        public async Task<IActionResult> DeleteAdapter(string name, CancellationToken cancellationToken)
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

        // GET /adapters
        [HttpGet]
        public async Task<IActionResult> ListAdapters(CancellationToken cancellationToken)
        {
            var adapters = await _managementService.ListAsync(HttpContext.User, cancellationToken).ConfigureAwait(false);
            return Ok(adapters);
        }
    }
}
