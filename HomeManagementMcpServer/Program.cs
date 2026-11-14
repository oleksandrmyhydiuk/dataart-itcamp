using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeManagement.Shared;

// Standalone Model Context Protocol (MCP) Server over stdio (JSON-RPC 2.0)
// Allows AI clients (Claude Desktop, Cursor, VS Code Copilot, Antigravity, MCP Inspector)
// to discover available smart home devices and interact with them.

var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    AllowAutoRedirect = true
};
using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true
};

var knownEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "http://localhost:1235",
    "http://localhost:1237"
};

using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
{
    AutoFlush = true
};

string? line;
while ((line = await stdin.ReadLineAsync()) is not null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    JsonObject? request;
    try
    {
        request = JsonNode.Parse(line) as JsonObject;
        if (request is null) continue;
    }
    catch
    {
        continue;
    }

    var method = request["method"]?.GetValue<string>() ?? string.Empty;
    var idNode = request["id"]?.DeepClone();

    // Notifications without an id do not expect a response
    if (idNode is null && method.StartsWith("notifications/", StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    var response = await HandleMcpRequestAsync(request, method, idNode);
    await stdout.WriteLineAsync(response.ToJsonString());
}

async Task<JsonObject> HandleMcpRequestAsync(JsonObject request, string method, JsonNode? idNode)
{
    try
    {
        var paramsObj = request["params"] as JsonObject;
        JsonNode result = method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject { ["listChanged"] = false }
                },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = "HomeManagementMcpServer",
                    ["version"] = "1.0.0"
                }
            },

            "ping" => new JsonObject(),

            "tools/list" => BuildToolsList(),

            "tools/call" => await ExecuteToolCallAsync(
                paramsObj?["name"]?.GetValue<string>() ?? string.Empty,
                paramsObj?["arguments"] as JsonObject ?? new JsonObject()),

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

JsonObject BuildToolsList() => new()
{
    ["tools"] = new JsonArray(
        new JsonObject
        {
            ["name"] = "discover_devices",
            ["description"] = "Finds available smart home devices on localhost ports (1234-1239) and returns their status, telemetry, and supported commands.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            }
        },
        new JsonObject
        {
            ["name"] = "get_device_info",
            ["description"] = "Returns live status, metrics (Temperature, UptimeSeconds), and available actions for a specific smart home device.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["device"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Device name ('Device1', 'Device2') or base URL ('http://localhost:1235')."
                    }
                },
                ["required"] = new JsonArray("device")
            }
        },
        new JsonObject
        {
            ["name"] = "execute_device_action",
            ["description"] = "Executes an action or command on a smart home device (e.g. 'INFO', 'Start Notepad', 'Start Calculator', 'Get Motion', 'Start Paint', 'RESTART', 'SHUTDOWN').",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["device"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Target device name ('Device1', 'Device2') or base URL."
                    },
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Action name from /config or command endpoint."
                    }
                },
                ["required"] = new JsonArray("device", "action")
            }
        })
};

async Task<JsonObject> ExecuteToolCallAsync(string toolName, JsonObject args)
{
    object payload;
    bool isError = false;

    switch (toolName)
    {
        case "discover_devices":
        {
            var candidatePorts = new[] { 1235, 1237, 1238, 1239 };
            foreach (var p in candidatePorts)
            {
                knownEndpoints.Add($"http://localhost:{p}");
            }

            var results = new List<object>();
            foreach (var ep in knownEndpoints)
            {
                var probe = await ProbeDeviceAsync(ep);
                if (probe is not null) results.Add(probe);
            }

            payload = new
            {
                discoveredCount = results.Count,
                devices = results
            };
            break;
        }

        case "get_device_info":
        {
            var target = args["device"]?.GetValue<string>() ?? "Device1";
            var url = ResolveDeviceUrl(target);
            var probe = await ProbeDeviceAsync(url);
            if (probe is null)
            {
                isError = true;
                payload = new { error = $"Device '{target}' ({url}) is offline or unreachable." };
            }
            else
            {
                payload = probe;
            }
            break;
        }

        case "execute_device_action":
        {
            var target = args["device"]?.GetValue<string>() ?? "Device1";
            var actionName = args["action"]?.GetValue<string>() ?? "INFO";
            var url = ResolveDeviceUrl(target);

            var config = await FetchConfigAsync(url);
            if (config is null)
            {
                isError = true;
                payload = new { error = $"Device '{target}' ({url}) is offline." };
                break;
            }

            var matched = config.Actions.FirstOrDefault(a =>
                string.Equals(a.Action, actionName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Command, actionName, StringComparison.OrdinalIgnoreCase) ||
                a.Action.Contains(actionName, StringComparison.OrdinalIgnoreCase))
                ?? new DeviceAction(actionName, CommandType.Post, actionName.ToLowerInvariant());

            var endpoint = $"{url.TrimEnd('/')}/{matched.Command.TrimStart('/')}";
            HttpResponseMessage resp;
            if (matched.CommandType == CommandType.Get)
            {
                resp = await httpClient.GetAsync(endpoint);
            }
            else
            {
                var content = !string.IsNullOrWhiteSpace(matched.CommandArgs)
                    ? new StringContent(matched.CommandArgs, Encoding.UTF8, "application/json")
                    : new StringContent(string.Empty, Encoding.UTF8, "application/json");
                resp = await httpClient.PostAsync(endpoint, content);
            }

            var text = await resp.Content.ReadAsStringAsync();
            payload = new
            {
                device = config.Name,
                endpointUrl = url,
                action = matched.Action,
                statusCode = (int)resp.StatusCode,
                output = string.IsNullOrWhiteSpace(text) ? "OK" : text.Trim()
            };
            break;
        }

        default:
            isError = true;
            payload = new { error = $"Unknown tool: {toolName}" };
            break;
    }

    return new JsonObject
    {
        ["isError"] = isError,
        ["content"] = new JsonArray(
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = JsonSerializer.Serialize(payload, jsonOptions)
            })
    };
}

string ResolveDeviceUrl(string deviceSpec)
{
    if (deviceSpec.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        deviceSpec.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        return deviceSpec.TrimEnd('/');
    }

    if (deviceSpec.Contains("2", StringComparison.OrdinalIgnoreCase))
    {
        return "http://localhost:1237";
    }

    return "http://localhost:1235";
}

async Task<object?> ProbeDeviceAsync(string baseUrl)
{
    try
    {
        var info = await httpClient.GetFromJsonAsync<DeviceInfo>($"{baseUrl}/info", jsonOptions);
        if (info is null) return null;
        var config = await FetchConfigAsync(baseUrl);

        return new
        {
            name = info.Name,
            endpointUrl = baseUrl,
            reportedAddress = info.Address,
            isOnline = true,
            metrics = info.Metrics,
            actions = config?.Actions.Select(a => new
            {
                action = a.Action,
                method = a.CommandType.ToString(),
                command = a.Command,
                commandArgs = a.CommandArgs
            })
        };
    }
    catch
    {
        return null;
    }
}

async Task<DeviceConfig?> FetchConfigAsync(string baseUrl)
{
    try
    {
        return await httpClient.GetFromJsonAsync<DeviceConfig>($"{baseUrl}/config", jsonOptions);
    }
    catch
    {
        return null;
    }
}
