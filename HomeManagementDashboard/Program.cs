using System.Text.Json.Nodes;
using HomeManagementDashboard.Components;
using HomeManagementDashboard.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<DeviceMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceMonitorService>());
builder.Services.AddSingleton<WorkflowEngineService>();
builder.Services.AddSingleton<McpServerService>();

var app = builder.Build();

// Ensure WorkflowEngineService is instantiated on startup so it subscribes to device events immediately
_ = app.Services.GetRequiredService<WorkflowEngineService>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();
app.MapStaticAssets();

// Summary endpoint
app.MapGet("/api/dashboard/summary", (DeviceMonitorService monitor, WorkflowEngineService workflows, McpServerService mcp) =>
{
    var devices = monitor.GetDevices();
    var rules = workflows.GetRules();
    return Results.Ok(new
    {
        totalDevices = monitor.TotalCount,
        onlineDevices = monitor.OnlineCount,
        offlineDevices = monitor.OfflineCount,
        lastUpdated = monitor.LastGlobalRefresh,
        mcpToolsCount = mcp.GetTools().Count,
        workflowsCount = rules.Count,
        enabledWorkflowsCount = rules.Count(r => r.IsEnabled),
        devices = devices.Select(d => new
        {
            name = d.EffectiveName,
            endpoint = d.EndpointUrl,
            reportedAddress = d.ReportedAddress,
            isOnline = d.IsOnline,
            responseTimeMs = d.ResponseTimeMs,
            temperatureCelsius = d.TemperatureCelsius,
            uptimeSeconds = d.UptimeSeconds,
            formattedUptime = d.FormattedUptime,
            actionsCount = d.Actions.Count
        })
    });
});

// Model Context Protocol (MCP) JSON-RPC 2.0 over HTTP endpoint
app.MapPost("/mcp", async (HttpRequest httpRequest, McpServerService mcpServer) =>
{
    var bodyNode = await JsonNode.ParseAsync(httpRequest.Body) as JsonObject;
    if (bodyNode is null)
    {
        return Results.BadRequest(new { error = "Invalid JSON-RPC request body." });
    }

    var response = await mcpServer.HandleJsonRpcAsync(bodyNode);
    return Results.Content(response.ToJsonString(), "application/json");
});

app.MapGet("/mcp/tools", (McpServerService mcpServer) =>
{
    var tools = mcpServer.GetTools().Select(t => new
    {
        name = t.Name,
        description = t.Description,
        inputSchema = t.InputSchema
    });
    return Results.Ok(new { server = "HomeManagement-MCP-Server", protocolVersion = "2024-11-05", tools });
});

app.MapGet("/api/workflows", (WorkflowEngineService workflows) =>
{
    return Results.Ok(new
    {
        rules = workflows.GetRules(),
        recentExecutions = workflows.GetExecutionHistory()
    });
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
