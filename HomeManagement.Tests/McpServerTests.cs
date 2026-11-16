using System.Text.Json.Nodes;
using HomeManagementDashboard.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeManagement.Tests;

public class McpServerTests
{
    private static (DeviceMonitorService Monitor, WorkflowEngineService Workflows, McpServerService Mcp) CreateMcpStack()
    {
        var configuration = new ConfigurationBuilder().Build();
        var monitor = new DeviceMonitorService(configuration, NullLogger<DeviceMonitorService>.Instance);
        var workflows = new WorkflowEngineService(monitor);
        var mcp = new McpServerService(monitor, workflows);
        return (monitor, workflows, mcp);
    }

    [Fact]
    public async Task HandleJsonRpc_Initialize_ReturnsProtocolVersionAndServerInfo()
    {
        var (monitor, workflows, mcp) = CreateMcpStack();
        using var _ = monitor;
        using var __ = workflows;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize"
        };

        var response = await mcp.HandleJsonRpcAsync(request);

        Assert.Equal("2.0", response["jsonrpc"]?.GetValue<string>());
        Assert.Equal(1, response["id"]?.GetValue<int>());
        Assert.Equal("2024-11-05", response["result"]?["protocolVersion"]?.GetValue<string>());
        Assert.Equal("HomeManagement-MCP-Server", response["result"]?["serverInfo"]?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task HandleJsonRpc_ToolsList_ReturnsAllRegisteredSmartHomeTools()
    {
        var (monitor, workflows, mcp) = CreateMcpStack();
        using var _ = monitor;
        using var __ = workflows;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/list"
        };

        var response = await mcp.HandleJsonRpcAsync(request);
        var toolsArray = response["result"]?["tools"] as JsonArray;

        Assert.NotNull(toolsArray);
        Assert.Equal(7, toolsArray!.Count);
    }

    [Fact]
    public async Task HandleJsonRpc_ToolsCall_CreateAndListWorkflows_Succeeds()
    {
        var (monitor, workflows, mcp) = CreateMcpStack();
        using var _ = monitor;
        using var __ = workflows;

        var createReq = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "create_workflow",
                ["arguments"] = new JsonObject
                {
                    ["name"] = "Unit Test Workflow",
                    ["triggerDevice"] = "Device1",
                    ["metricField"] = "PowerWatts",
                    ["operator"] = "GreaterThan",
                    ["threshold"] = "140.0",
                    ["targetDevice"] = "Device1",
                    ["actionCommand"] = "INFO"
                }
            }
        };

        var createResp = await mcp.HandleJsonRpcAsync(createReq);
        var isError = createResp["result"]?["isError"]?.GetValue<bool>() ?? true;
        var text = createResp["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;

        Assert.False(isError);
        Assert.Contains("Unit Test Workflow", text);
        Assert.Contains(workflows.GetRules(), r => r.Name == "Unit Test Workflow");
    }

    [Fact]
    public async Task ProcessNaturalLanguagePromptAsync_SelectsAppropriateMcpTool()
    {
        var (monitor, workflows, mcp) = CreateMcpStack();
        using var _ = monitor;
        using var __ = workflows;

        var turn = await mcp.ProcessNaturalLanguagePromptAsync("Покажи всі сценарії автоматизації workflow");

        Assert.Equal("list_workflows", turn.ToolCalled);
        Assert.NotEmpty(mcp.GetChatHistory());
    }
}
