using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Foundry;
using Microsoft.McpGateway.Management.Service;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Service.Authentication;
using Microsoft.McpGateway.Service.Controllers;
using OpenAI.Chat;

namespace Microsoft.McpGateway.Service.Tests;

[TestClass]
public class BuiltinToolsE2ETests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] Builtins = ["builtin:bash", "builtin:read_file", "builtin:write_file"];

    [DataTestMethod]
    [DataRow(false, "mcp.admin")]
    [DataRow(false, "mcp.builtin")]
    [DataRow(true, "mcp.admin")]
    [DataRow(true, "mcp.builtin")]
    [DataRow(true, "mcp.dev")]
    public async Task HttpCreateAndUpdate_RejectBuiltins_RegardlessOfRoleConfiguration(bool configureRole, string role)
    {
        await using var gateway = await TestGateway.StartAsync(configureRole, role);
        using var created = await gateway.Client.PostAsJsonAsync("agents", Agent("editable-agent"));
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        foreach (var builtin in Builtins)
        {
            var name = "disabled-" + builtin["builtin:".Length..].Replace('_', '-');
            using var rejectedCreate = await gateway.Client.PostAsJsonAsync("agents", Agent(name, builtin));
            rejectedCreate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await rejectedCreate.Content.ReadAsStringAsync()).Should().Contain("Built-in tools are disabled.");

            using var missing = await gateway.Client.GetAsync($"agents/{name}");
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

            using var rejectedUpdate = await gateway.Client.PutAsJsonAsync("agents/editable-agent", Agent("editable-agent", builtin));
            rejectedUpdate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await rejectedUpdate.Content.ReadAsStringAsync()).Should().Contain("Built-in tools are disabled.");
        }

        var unchanged = await gateway.Client.GetFromJsonAsync<JsonElement>("agents/editable-agent");
        unchanged.GetProperty("tools").GetArrayLength().Should().Be(0);
        using var deleted = await gateway.Client.DeleteAsync("agents/editable-agent");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [DataTestMethod]
    [DataRow(false, "mcp.admin")]
    [DataRow(true, "mcp.builtin")]
    public async Task HttpExistingAgentAndSession_RejectBuiltinCalls_AndKeepSubagentsWorking(bool configureRole, string role)
    {
        await using var gateway = await TestGateway.StartAsync(configureRole, role);
        var store = gateway.App.Services.GetRequiredService<IAgentResourceStore>();
        await store.UpsertAsync(AgentResource.Create(Agent("child-agent"), "e2e-user", DateTimeOffset.UtcNow), CancellationToken.None);
        var legacy = Agent("existing-agent", [.. Builtins, "agent:child-agent"]);
        await store.UpsertAsync(AgentResource.Create(legacy, "e2e-user", DateTimeOffset.UtcNow), CancellationToken.None);

        using var existing = await gateway.Client.GetAsync("agents/existing-agent");
        existing.StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await gateway.Client.PostAsJsonAsync("sessions/run", new { agentName = legacy.Name, input = "Run the fixture." });
        var events = await ReadEventsAsync(response);
        events.Should().ContainSingle(item => item.Type == SessionEventType.Completed && item.Answer == "done");
        events.Should().NotContain(item => item.Type == SessionEventType.Failed);

        var calls = events.Where(item => item.Type == SessionEventType.ToolCallCompleted).ToList();
        var disabledCalls = calls.Where(item => item.ToolName!.StartsWith("builtin_", StringComparison.Ordinal)).ToList();
        disabledCalls.Should().HaveCount(3);
        disabledCalls.Should().OnlyContain(item => !string.IsNullOrEmpty(item.Error));
        calls.Should().ContainSingle(item => item.ToolName == "agent_child-agent" && item.Error == null);
        gateway.Model.AdvertisedTools.Should().OnlyContain(names => names.All(name => !name.StartsWith("builtin_", StringComparison.Ordinal)));
        gateway.Model.AdvertisedTools.Should().Contain(names => names.Contains("agent_child-agent"));

        var sessionId = events.Single(item => item.Type == SessionEventType.Started).SessionId;
        var sessionStore = gateway.App.Services.GetRequiredService<ISessionResourceStore>();
        var session = await sessionStore.TryGetAsync(sessionId, CancellationToken.None);
        session.Should().NotBeNull();
        session!.Status.Should().Be(SessionStatus.Completed);
        Directory.GetFileSystemEntries(session.WorkingDirectory!).Should().BeEmpty();

        using var continued = await gateway.Client.PostAsJsonAsync($"sessions/{sessionId}/messages", new { input = "Continue the fixture." });
        (await ReadEventsAsync(continued)).Should().ContainSingle(item => item.Type == SessionEventType.Completed && item.Answer == "done");
        gateway.Model.AdvertisedTools.Should().OnlyContain(names => names.All(name => !name.StartsWith("builtin_", StringComparison.Ordinal)));

        using var rejectedUpdate = await gateway.Client.PutAsJsonAsync("agents/existing-agent", legacy);
        rejectedUpdate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var removedBuiltins = await gateway.Client.PutAsJsonAsync("agents/existing-agent", Agent(legacy.Name, "agent:child-agent"));
        removedBuiltins.StatusCode.Should().Be(HttpStatusCode.OK);
        using var deletedSession = await gateway.Client.DeleteAsync($"sessions/{sessionId}");
        deletedSession.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Directory.Exists(session.WorkingDirectory).Should().BeFalse();
        using var deletedAgent = await gateway.Client.DeleteAsync("agents/existing-agent");
        deletedAgent.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static AgentData Agent(string name, params string[] tools) => new()
    {
        Name = name,
        Model = "local-fixture",
        System = "Use the deterministic test fixture.",
        Tools = tools,
    };

    private static async Task<List<SessionEvent>> ReadEventsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("event: error").And.NotContain("event: forbidden");
        return body.Split('\n')
            .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonSerializer.Deserialize<SessionEvent>(line[6..], JsonOptions)!)
            .ToList();
    }

    private sealed class TestGateway(WebApplication app, HttpClient client, FixtureChatClient model) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public HttpClient Client { get; } = client;
        public FixtureChatClient Model { get; } = model;

        public static async Task<TestGateway> StartAsync(bool configureRole, string role)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development",
                ApplicationName = typeof(AgentManagementController).Assembly.GetName().Name,
            });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, null);
            builder.Services.AddAuthorization();
            builder.Services.AddControllers().AddApplicationPart(typeof(AgentManagementController).Assembly);
            builder.Services.Configure<BuiltinToolSettings>(options => options.RequiredRoles = configureRole ? ["mcp.builtin"] : []);
            builder.Services.AddSingleton<IBuiltinToolAuthorizer, BuiltinToolAuthorizer>();
            builder.Services.AddSingleton<IPermissionProvider, SimplePermissionProvider>();
            builder.Services.AddSingleton<IAgentResourceStore, InMemoryAgentResourceStore>();
            builder.Services.AddSingleton<ISessionResourceStore, InMemorySessionResourceStore>();
            builder.Services.AddSingleton<IToolResourceStore, InMemoryToolResourceStore>();
            builder.Services.AddSingleton<IAgentManagementService, AgentManagementService>();
            builder.Services.AddSingleton<ISessionManagementService, SessionManagementService>();
            builder.Services.AddSingleton<BuiltinToolExecutor>();
            builder.Services.AddSingleton<AgentToolRegistry>();
            builder.Services.AddSingleton<AgentRunner>();
            builder.Services.AddSingleton<SubAgentInvoker>();
            builder.Services.AddSingleton<Func<AgentRunner>>(services => () => services.GetRequiredService<AgentRunner>());
            builder.Services.AddHttpClient();
            var model = new FixtureChatClient();
            builder.Services.AddSingleton<IFoundryChatClient>(model);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            var client = new HttpClient { BaseAddress = new Uri(addresses.Addresses.Single()), Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("X-Dev-UserId", "e2e-user");
            client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
            return new TestGateway(app, client, model);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await App.StopAsync();
                var sessions = await App.Services.GetRequiredService<ISessionResourceStore>().ListAsync(CancellationToken.None);
                foreach (var session in sessions)
                {
                    if (!string.IsNullOrEmpty(session.WorkingDirectory) && Directory.Exists(session.WorkingDirectory))
                        Directory.Delete(session.WorkingDirectory, recursive: true);
                }
            }
            finally
            {
                await App.DisposeAsync();
            }
        }
    }

    private sealed class FixtureChatClient : IFoundryChatClient
    {
        private bool _sentToolCalls;
        public List<string[]> AdvertisedTools { get; } = [];

        public Task<ChatCompletion> CompleteAsync(IList<ChatMessage> messages, IEnumerable<ChatTool> tools, string? deploymentNameOverride, CancellationToken cancellationToken)
            => throw new NotSupportedException("The fixture uses streaming completions.");

        public async IAsyncEnumerable<StreamingChatCompletionUpdate> CompleteStreamingAsync(
            IList<ChatMessage> messages,
            IEnumerable<ChatTool> tools,
            string? deploymentNameOverride,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdvertisedTools.Add(tools.Select(tool => tool.FunctionName).ToArray());
            await Task.CompletedTask;
            if (!_sentToolCalls)
            {
                _sentToolCalls = true;
                var arguments = BinaryData.FromString("{\"command\":\"printf executed > marker.txt\",\"path\":\"marker.txt\",\"content\":\"executed\"}");
                yield return OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                    completionId: "fixture-calls",
                    toolCallUpdates: [
                        OpenAIChatModelFactory.StreamingChatToolCallUpdate(0, "call-bash", ChatToolCallKind.Function, BuiltinToolExecutor.Bash, arguments),
                        OpenAIChatModelFactory.StreamingChatToolCallUpdate(1, "call-read", ChatToolCallKind.Function, BuiltinToolExecutor.ReadFile, arguments),
                        OpenAIChatModelFactory.StreamingChatToolCallUpdate(2, "call-write", ChatToolCallKind.Function, BuiltinToolExecutor.WriteFile, arguments),
                        OpenAIChatModelFactory.StreamingChatToolCallUpdate(3, "call-child", ChatToolCallKind.Function, "agent_child-agent", BinaryData.FromString("{\"input\":\"Run the child fixture.\"}")),
                    ],
                    finishReason: ChatFinishReason.ToolCalls);
            }
            else
            {
                yield return OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                    completionId: "fixture-response",
                    contentUpdate: new ChatMessageContent(ChatMessageContentPart.CreateTextPart("done")),
                    finishReason: ChatFinishReason.Stop);
            }
        }
    }
}