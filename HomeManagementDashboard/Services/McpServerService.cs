using System.Text.Json;
using System.Text.Json.Nodes;
using HomeManagement.Shared;
using HomeManagementDashboard.Models;

namespace HomeManagementDashboard.Services;

public record McpToolDescriptor(
    string Name,
    string Description,
    JsonObject InputSchema);

public record AiChatTurn(
    DateTime Timestamp,
    string UserPrompt,
    string ToolCalled,
    string McpRequestJson,
    string McpResponseJson,
    string AssistantReply);

public class McpServerService
{
    private readonly DeviceMonitorService _monitor;
    private readonly WorkflowEngineService _workflows;
    private readonly List<AiChatTurn> _chatHistory = new();
    private readonly Lock _syncLock = new();

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public event Action? OnMcpActivityChanged;

    public McpServerService(DeviceMonitorService monitor, WorkflowEngineService workflows)
    {
        _monitor = monitor;
        _workflows = workflows;
    }

    public IReadOnlyList<AiChatTurn> GetChatHistory()
    {
        lock (_syncLock)
        {
            return _chatHistory.ToList();
        }
    }

    public IReadOnlyList<McpToolDescriptor> GetTools() =>
    [
        new McpToolDescriptor(
            "discover_devices",
            "Scans local network ports (1234-1239, 5000-5001) to find available smart home devices and register them.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            }),

        new McpToolDescriptor(
            "list_devices",
            "Lists all known smart home devices with their online status, IP address, temperature (°C), uptime, and supported actions.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["onlineOnly"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "If true, returns only currently online devices."
                    }
                }
            }),

        new McpToolDescriptor(
            "get_device_details",
            "Refreshes and returns detailed telemetry (/info) and available commands (/config) for a specific smart home device.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["device"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Device name (e.g. 'Device1', 'Device2') or endpoint URL (e.g. 'http://localhost:1235')."
                    }
                },
                ["required"] = new JsonArray("device")
            }),

        new McpToolDescriptor(
            "execute_device_action",
            "Executes a remote action or system command on a smart home device (e.g. 'INFO', 'Start Notepad', 'Start Calculator', 'Get Motion', 'Start Paint', 'RESTART', 'SHUTDOWN').",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["device"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Target device name (e.g. 'Device1') or URL."
                    },
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name of the action from /config (e.g. 'INFO', 'Start Calculator', 'Get Motion', 'Start Paint')."
                    }
                },
                ["required"] = new JsonArray("device", "action")
            }),

        new McpToolDescriptor(
            "list_workflows",
            "Lists all configured smart home automation workflows (triggers, conditions, and actions).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            }),

        new McpToolDescriptor(
            "create_workflow",
            "Creates a new smart home automation workflow rule with a trigger, condition, and action.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Scenario name" },
                    ["triggerDevice"] = new JsonObject { ["type"] = "string", ["description"] = "Device name (e.g. 'Device1' or '*')" },
                    ["metricField"] = new JsonObject { ["type"] = "string", ["description"] = "TemperatureCelsius, UptimeSeconds, ResponseTimeMs, IsOnline, or OnlineDevicesCount" },
                    ["operator"] = new JsonObject { ["type"] = "string", ["description"] = "GreaterThan, LessThan, Equals, etc." },
                    ["threshold"] = new JsonObject { ["type"] = "string", ["description"] = "Target value (e.g. '44.5')" },
                    ["targetDevice"] = new JsonObject { ["type"] = "string", ["description"] = "Target device for action (e.g. 'Device1')" },
                    ["actionCommand"] = new JsonObject { ["type"] = "string", ["description"] = "Action to run (e.g. 'INFO', 'Get Motion', 'Start Calculator')" }
                },
                ["required"] = new JsonArray("name", "triggerDevice", "threshold", "actionCommand")
            }),

        new McpToolDescriptor(
            "trigger_workflow",
            "Manually triggers a smart home automation workflow by ID or name.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["workflow"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Workflow ID or name."
                    }
                },
                ["required"] = new JsonArray("workflow")
            })
    ];

    public async Task<JsonObject> HandleJsonRpcAsync(JsonObject request)
    {
        var idNode = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>() ?? string.Empty;
        var paramsObj = request["params"] as JsonObject;

        try
        {
            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = false },
                        ["resources"] = new JsonObject { ["listChanged"] = false }
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "HomeManagement-MCP-Server",
                        ["version"] = "1.0.0"
                    }
                },

                "notifications/initialized" or "ping" => new JsonObject(),

                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray(
                        GetTools().Select(t => (JsonNode)new JsonObject
                        {
                            ["name"] = t.Name,
                            ["description"] = t.Description,
                            ["inputSchema"] = t.InputSchema.DeepClone()
                        }).ToArray())
                },

                "tools/call" => await HandleToolCallInternalAsync(
                    paramsObj?["name"]?.GetValue<string>() ?? string.Empty,
                    paramsObj?["arguments"] as JsonObject ?? new JsonObject()),

                "resources/list" => new JsonObject
                {
                    ["resources"] = new JsonArray(
                        new JsonObject
                        {
                            ["uri"] = "homemanagement://devices/summary",
                            ["name"] = "Smart Home Devices Summary",
                            ["mimeType"] = "application/json",
                            ["description"] = "Live snapshot of all smart home devices and their status."
                        },
                        new JsonObject
                        {
                            ["uri"] = "homemanagement://workflows/list",
                            ["name"] = "Smart Home Automation Workflows",
                            ["mimeType"] = "application/json",
                            ["description"] = "Configured automation rules (triggers, conditions, actions)."
                        })
                },

                _ => throw new InvalidOperationException($"Unsupported MCP method: {method}")
            };

            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idNode,
                ["result"] = result
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idNode,
                ["error"] = new JsonObject
                {
                    ["code"] = -32603,
                    ["message"] = ex.Message
                }
            };
        }
    }

    public async Task<JsonObject> CallToolDirectAsync(string toolName, JsonObject arguments)
    {
        return await HandleToolCallInternalAsync(toolName, arguments);
    }

    private async Task<JsonObject> HandleToolCallInternalAsync(string toolName, JsonObject args)
    {
        _monitor.AddExternalLog("MCP Server", $"Tool [{toolName}]", $"AI-клієнт викликав інструмент '{toolName}'");

        object payload;
        bool isError = false;

        switch (toolName)
        {
            case "discover_devices":
            {
                var newlyFound = await _monitor.DiscoverLocalhostDevicesAsync();
                await _monitor.RefreshAllAsync();
                var devices = _monitor.GetDevices();
                payload = new
                {
                    newlyDiscoveredCount = newlyFound,
                    totalDevices = devices.Count,
                    onlineDevices = devices.Count(d => d.IsOnline),
                    devices = devices.Select(FormatDeviceSummary)
                };
                break;
            }

            case "list_devices":
            {
                await _monitor.RefreshAllAsync();
                var onlineOnly = args["onlineOnly"]?.GetValue<bool>() ?? false;
                var devices = _monitor.GetDevices()
                    .Where(d => !onlineOnly || d.IsOnline)
                    .ToList();

                payload = new
                {
                    totalMonitored = _monitor.TotalCount,
                    onlineDevicesCount = _monitor.OnlineCount,
                    offlineDevicesCount = _monitor.OfflineCount,
                    devices = devices.Select(FormatDeviceSummary)
                };
                break;
            }

            case "get_device_details":
            {
                var target = args["device"]?.GetValue<string>() ?? "Device1";
                var dev = _monitor.FindDevice(target);
                if (dev is null)
                {
                    isError = true;
                    payload = new { error = $"Device '{target}' not found." };
                }
                else
                {
                    await _monitor.PollDeviceAsync(dev);
                    payload = FormatDeviceSummary(dev);
                }
                break;
            }

            case "execute_device_action":
            {
                var target = args["device"]?.GetValue<string>() ?? "Device1";
                var actionName = args["action"]?.GetValue<string>() ?? "INFO";
                var dev = _monitor.FindDevice(target);

                if (dev is null)
                {
                    isError = true;
                    payload = new { error = $"Device '{target}' not found." };
                }
                else if (!dev.IsOnline)
                {
                    isError = true;
                    payload = new { error = $"Device '{dev.EffectiveName}' is currently OFFLINE." };
                }
                else
                {
                    var matched = dev.Actions.FirstOrDefault(a =>
                        string.Equals(a.Action, actionName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a.Command, actionName, StringComparison.OrdinalIgnoreCase) ||
                        a.Action.Contains(actionName, StringComparison.OrdinalIgnoreCase));

                    matched ??= new DeviceAction(
                        actionName,
                        actionName.Equals("info", StringComparison.OrdinalIgnoreCase) ? CommandType.Get : CommandType.Post,
                        actionName.ToLowerInvariant());

                    var output = await _monitor.ExecuteActionAsync(dev, matched);
                    payload = new
                    {
                        device = dev.EffectiveName,
                        executedAction = matched.Action,
                        httpMethod = matched.CommandType.ToString(),
                        success = !dev.LastActionIsError,
                        output
                    };
                }
                break;
            }

            case "list_workflows":
            {
                var rules = _workflows.GetRules();
                payload = new
                {
                    totalWorkflows = rules.Count,
                    enabledWorkflows = rules.Count(r => r.IsEnabled),
                    workflows = rules.Select(r => new
                    {
                        id = r.Id,
                        name = r.Name,
                        description = r.Description,
                        isEnabled = r.IsEnabled,
                        trigger = r.DescribeTrigger(),
                        condition = r.Condition.Describe(),
                        action = r.Action.Describe(),
                        executionCount = r.ExecutionCount,
                        lastTriggered = r.LastTriggeredLocal
                    })
                };
                break;
            }

            case "create_workflow":
            {
                var name = args["name"]?.GetValue<string>() ?? "AI Smart Scenario";
                var triggerDev = args["triggerDevice"]?.GetValue<string>() ?? "Device1";
                var metricStr = args["metricField"]?.GetValue<string>() ?? "TemperatureCelsius";
                var opStr = args["operator"]?.GetValue<string>() ?? "GreaterThan";
                var threshold = args["threshold"]?.GetValue<string>() ?? "44.0";
                var targetDev = args["targetDevice"]?.GetValue<string>() ?? triggerDev;
                var actionCmd = args["actionCommand"]?.GetValue<string>() ?? "INFO";

                _ = Enum.TryParse<WorkflowMetricField>(metricStr, true, out var parsedField);
                if (!Enum.TryParse<ComparisonOperator>(opStr, true, out var parsedOp))
                {
                    parsedOp = ComparisonOperator.GreaterThan;
                }

                var created = _workflows.AddRule(new WorkflowRule
                {
                    Name = name,
                    Description = $"Створено через MCP AI-інтеграцію ({triggerDev})",
                    TriggerType = WorkflowTriggerType.TelemetryUpdate,
                    TriggerDeviceFilter = triggerDev,
                    Condition = new WorkflowCondition
                    {
                        Field = parsedField,
                        Operator = parsedOp,
                        TargetValue = threshold
                    },
                    Action = new WorkflowActionDefinition
                    {
                        ActionType = WorkflowActionType.ExecuteDeviceAction,
                        TargetDeviceNameOrUrl = targetDev,
                        CommandOrActionName = actionCmd
                    }
                });

                payload = new
                {
                    created = true,
                    workflowId = created.Id,
                    name = created.Name,
                    condition = created.Condition.Describe(),
                    action = created.Action.Describe()
                };
                break;
            }

            case "trigger_workflow":
            {
                var wf = args["workflow"]?.GetValue<string>() ?? string.Empty;
                var (success, output) = await _workflows.TriggerRuleManuallyAsync(wf);
                isError = !success;
                payload = new
                {
                    workflow = wf,
                    success,
                    output
                };
                break;
            }

            default:
                isError = true;
                payload = new { error = $"Unknown MCP tool '{toolName}'." };
                break;
        }

        var textJson = JsonSerializer.Serialize(payload, PrettyJson);
        return new JsonObject
        {
            ["isError"] = isError,
            ["content"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = textJson
                })
        };
    }

    public async Task<AiChatTurn> ProcessNaturalLanguagePromptAsync(string prompt)
    {
        var q = (prompt ?? string.Empty).Trim();
        var lower = q.ToLowerInvariant();

        string toolName;
        var toolArgs = new JsonObject();

        string targetDevice = lower.Contains("device2") || lower.Contains("1237") || lower.Contains("другий") || lower.Contains("другому")
            ? "Device2"
            : "Device1";

        if (lower.Contains("знайд") || lower.Contains("пошук") || lower.Contains("discover") || lower.Contains("scan"))
        {
            toolName = "discover_devices";
        }
        else if (lower.Contains("створ") && (lower.Contains("сценарій") || lower.Contains("правило") || lower.Contains("workflow")))
        {
            toolName = "create_workflow";
            toolArgs["name"] = "AI: Авто-моніторинг температури";
            toolArgs["triggerDevice"] = targetDevice;
            toolArgs["metricField"] = "TemperatureCelsius";
            toolArgs["operator"] = "GreaterThan";
            toolArgs["threshold"] = "43.5";
            toolArgs["targetDevice"] = targetDevice;
            toolArgs["actionCommand"] = "INFO";
        }
        else if (lower.Contains("сценарі") || lower.Contains("workflow") || lower.Contains("автоматизац"))
        {
            toolName = "list_workflows";
        }
        else if (lower.Contains("калькулятор") || lower.Contains("calc"))
        {
            toolName = "execute_device_action";
            toolArgs["device"] = targetDevice;
            toolArgs["action"] = "Start Calculator";
        }
        else if (lower.Contains("блокнот") || lower.Contains("notepad"))
        {
            toolName = "execute_device_action";
            toolArgs["device"] = targetDevice;
            toolArgs["action"] = "Start Notepad";
        }
        else if (lower.Contains("paint") || lower.Contains("пейнт"))
        {
            toolName = "execute_device_action";
            toolArgs["device"] = "Device2";
            toolArgs["action"] = "Start Paint";
        }
        else if (lower.Contains("рух") || lower.Contains("motion") || lower.Contains("git"))
        {
            toolName = "execute_device_action";
            toolArgs["device"] = targetDevice;
            toolArgs["action"] = "Get Motion";
        }
        else if (lower.Contains("детал") || (lower.Contains("device") && (lower.Contains("1") || lower.Contains("2"))))
        {
            toolName = "get_device_details";
            toolArgs["device"] = targetDevice;
        }
        else
        {
            toolName = "list_devices";
            toolArgs["onlineOnly"] = false;
        }

        var rpcRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString("N")[..6],
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = toolArgs.DeepClone()
            }
        };

        var rpcResponse = await HandleJsonRpcAsync(rpcRequest);
        var replySummary = BuildHumanReply(toolName, toolArgs);

        var turn = new AiChatTurn(
            DateTime.Now,
            q,
            toolName,
            rpcRequest.ToJsonString(PrettyJson),
            rpcResponse.ToJsonString(PrettyJson),
            replySummary);

        lock (_syncLock)
        {
            _chatHistory.Insert(0, turn);
            if (_chatHistory.Count > 20)
            {
                _chatHistory.RemoveAt(_chatHistory.Count - 1);
            }
        }

        OnMcpActivityChanged?.Invoke();
        return turn;
    }

    private string BuildHumanReply(string toolName, JsonObject args)
    {
        return toolName switch
        {
            "discover_devices" =>
                $"Виконано сканування локальної мережі через MCP-інструмент `discover_devices`. Активних пристроїв онлайн: {_monitor.OnlineCount} із {_monitor.TotalCount}.",
            "list_devices" =>
                $"Отримано перелік пристроїв через MCP `list_devices`: онлайн {_monitor.OnlineCount} із {_monitor.TotalCount} ({string.Join(", ", _monitor.GetDevices().Select(d => $"{d.EffectiveName}: {(d.IsOnline ? $"{d.TemperatureCelsius:0.0}°C" : "OFFLINE")}"))})",
            "get_device_details" =>
                $"Отримано детальну телеметрію та конфігурацію для `{args["device"]}` через MCP `get_device_details`.",
            "execute_device_action" =>
                $"Через MCP `execute_device_action` виконано дію `{args["action"]}` на пристрої `{args["device"]}`.",
            "list_workflows" =>
                $"Через MCP `list_workflows` отримано список сценаріїв автоматизації (всього: {_workflows.GetRules().Count}).",
            "create_workflow" =>
                $"Через MCP `create_workflow` створено новий сценарій автоматизації `{args["name"]}`.",
            _ => $"Виконано MCP-інструмент `{toolName}`."
        };
    }

    private static object FormatDeviceSummary(MonitoredDeviceState d) => new
    {
        name = d.EffectiveName,
        endpointUrl = d.EndpointUrl,
        reportedAddress = d.ReportedAddress,
        isOnline = d.IsOnline,
        responseTimeMs = d.ResponseTimeMs,
        temperatureCelsius = d.TemperatureCelsius,
        humidityPercent = d.HumidityPercent,
        powerWatts = d.PowerWatts,
        co2Ppm = d.Co2Ppm,
        uptimeSeconds = d.UptimeSeconds,
        formattedUptime = d.FormattedUptime,
        availableActions = d.Actions.Select(a => new
        {
            name = a.Action,
            method = a.CommandType.ToString(),
            endpoint = $"/{a.Command}",
            arguments = a.CommandArgs
        })
    };
}
