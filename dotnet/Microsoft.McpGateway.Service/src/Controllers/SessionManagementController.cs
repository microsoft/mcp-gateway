// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Foundry;
using Microsoft.McpGateway.Management.Service;

namespace Microsoft.McpGateway.Service.Controllers
{
    /// <summary>
    /// Controller for managing sessions (one execution of an agent definition).
    /// </summary>
    [ApiController]
    [Route("sessions")]
    [Authorize]
    public class SessionManagementController(ISessionManagementService managementService) : ControllerBase
    {
        private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);

        private readonly ISessionManagementService _managementService = managementService ?? throw new ArgumentNullException(nameof(managementService));

        // POST /sessions
        [HttpPost]
        public async Task<IActionResult> CreateSession([FromBody] SessionData request, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _managementService.CreateAsync(HttpContext.User, request, cancellationToken).ConfigureAwait(false);
                return CreatedAtAction(nameof(GetSession), new { id = result.Id }, result);
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

        // POST /sessions/run — synchronous, server-sent-events stream.
        // Each event is written as `event: <type>\ndata: <json>\n\n` so a
        // browser EventSource (or any line-buffered SSE reader) can consume it.
        [HttpPost("run")]
        public async Task RunSessionStream([FromBody] SessionData request, CancellationToken cancellationToken)
        {
            Response.StatusCode = 200;
            Response.ContentType = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";

            try
            {
                await foreach (var evt in _managementService.RunStreamingAsync(HttpContext.User, request, cancellationToken).ConfigureAwait(false))
                {
                    var json = JsonSerializer.Serialize(evt, SseJsonOptions);
                    var payload = $"event: {evt.Type}\ndata: {json}\n\n";
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    await Response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (ArgumentException ex)
            {
                await WriteSseErrorAsync("error", ex.Message, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await WriteSseErrorAsync("forbidden", "You do not have permission to invoke this agent.", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await WriteSseErrorAsync("error", ex.Message, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task WriteSseErrorAsync(string eventName, string message, CancellationToken cancellationToken)
        {
            var payload = $"event: {eventName}\ndata: {JsonSerializer.Serialize(new { error = message }, SseJsonOptions)}\n\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            try
            {
                await Response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Client likely disconnected; nothing more to do.
            }
        }

        // POST /sessions/{id}/messages — append a user message to an existing
        // session and stream the agent's reply over SSE. The session must
        // already exist; prior User/Assistant messages are replayed as context.
        public sealed class ContinueSessionBody
        {
            public string? Input { get; set; }
        }

        [HttpPost("{id}/messages")]
        public async Task ContinueSession(string id, [FromBody] ContinueSessionBody request, CancellationToken cancellationToken)
        {
            Response.StatusCode = 200;
            Response.ContentType = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";

            var input = request?.Input?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                await WriteSseErrorAsync("error", "Request body must include a non-empty 'input' field.", cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await foreach (var evt in _managementService.ContinueStreamingAsync(HttpContext.User, id, input, cancellationToken).ConfigureAwait(false))
                {
                    var json = JsonSerializer.Serialize(evt, SseJsonOptions);
                    var payload = $"event: {evt.Type}\ndata: {json}\n\n";
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    await Response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (ArgumentException ex)
            {
                await WriteSseErrorAsync("error", ex.Message, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await WriteSseErrorAsync("forbidden", "You do not have permission to continue this session.", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await WriteSseErrorAsync("error", ex.Message, cancellationToken).ConfigureAwait(false);
            }
        }

        // GET /sessions/{id}
        [HttpGet("{id}")]
        public async Task<IActionResult> GetSession(string id, CancellationToken cancellationToken)
        {
            try
            {
                var session = await _managementService.GetAsync(HttpContext.User, id, cancellationToken).ConfigureAwait(false);
                if (session == null)
                    return NotFound();
                return Ok(session);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }

        // DELETE /sessions/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteSession(string id, CancellationToken cancellationToken)
        {
            try
            {
                await _managementService.DeleteAsync(HttpContext.User, id, cancellationToken).ConfigureAwait(false);
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

        // GET /sessions
        [HttpGet]
        public async Task<IActionResult> ListSessions(CancellationToken cancellationToken)
        {
            var sessions = await _managementService.ListAsync(HttpContext.User, cancellationToken).ConfigureAwait(false);
            return Ok(sessions);
        }
    }
}
