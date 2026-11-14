using System.Globalization;
using HomeManagement.Shared;
using HomeManagementDashboard.Models;

namespace HomeManagementDashboard.Services;

public class WorkflowEngineService : IDisposable
{
    private readonly DeviceMonitorService _monitor;
    private readonly List<WorkflowRule> _rules = new();
    private readonly List<WorkflowExecutionLog> _history = new();
    private readonly Lock _syncLock = new();

    public event Action? OnWorkflowStateChanged;

    public WorkflowEngineService(DeviceMonitorService monitor)
    {
        _monitor = monitor;
        SeedDefaultScenarios();
        _monitor.OnDevicePolledAsync += HandleDevicePolledAsync;
    }

    private void SeedDefaultScenarios()
    {
        _rules.Add(new WorkflowRule
        {
            Id = "wf-temp-guard",
            Name = "Контроль перегріву Device1",
            Description = "Автоматично запрашиває діагностику (INFO), якщо температура Device1 перевищує 44.0 °C",
            IsEnabled = true,
            TriggerType = WorkflowTriggerType.TelemetryUpdate,
            TriggerDeviceFilter = "Device1",
            CooldownSeconds = 30,
            Condition = new WorkflowCondition
            {
                Field = WorkflowMetricField.TemperatureCelsius,
                Operator = ComparisonOperator.GreaterThan,
                TargetValue = "44.0"
            },
            Action = new WorkflowActionDefinition
            {
                ActionType = WorkflowActionType.ExecuteDeviceAction,
                TargetDeviceNameOrUrl = "Device1",
                CommandOrActionName = "INFO"
            }
        });

        _rules.Add(new WorkflowRule
        {
            Id = "wf-motion-check",
            Name = "Перевірка датчика руху (Get Motion)",
            Description = "Виконує команду 'Get Motion' на Device1, коли Uptime пристрою перевищує 60 секунд",
            IsEnabled = true,
            TriggerType = WorkflowTriggerType.TelemetryUpdate,
            TriggerDeviceFilter = "Device1",
            CooldownSeconds = 60,
            Condition = new WorkflowCondition
            {
                Field = WorkflowMetricField.UptimeSeconds,
                Operator = ComparisonOperator.GreaterThan,
                TargetValue = "60"
            },
            Action = new WorkflowActionDefinition
            {
                ActionType = WorkflowActionType.ExecuteDeviceAction,
                TargetDeviceNameOrUrl = "Device1",
                CommandOrActionName = "Get Motion"
            }
        });

        _rules.Add(new WorkflowRule
        {
            Id = "wf-offline-alert",
            Name = "Сповіщення про втрату зв'язку з пристроєм",
            Description = "Генерує системне сповіщення у панелі, якщо будь-який пристрій переходить у стан Offline",
            IsEnabled = true,
            TriggerType = WorkflowTriggerType.DeviceBecameOffline,
            TriggerDeviceFilter = "*",
            CooldownSeconds = 15,
            Condition = new WorkflowCondition
            {
                Field = WorkflowMetricField.IsOnline,
                Operator = ComparisonOperator.Equals,
                TargetValue = "false"
            },
            Action = new WorkflowActionDefinition
            {
                ActionType = WorkflowActionType.DashboardNotification,
                TargetDeviceNameOrUrl = "*",
                CommandOrActionName = "Alert",
                NotificationMessage = "Увага! Зафіксовано перехід пристрою в стан OFFLINE."
            }
        });
    }

    public IReadOnlyList<WorkflowRule> GetRules()
    {
        lock (_syncLock)
        {
            return _rules.ToList();
        }
    }

    public IReadOnlyList<WorkflowExecutionLog> GetExecutionHistory()
    {
        lock (_syncLock)
        {
            return _history.ToList();
        }
    }

    public WorkflowRule AddRule(WorkflowRule rule)
    {
        lock (_syncLock)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                rule.Id = Guid.NewGuid().ToString("N")[..8];
            }
            _rules.Add(rule);
        }

        _monitor.AddExternalLog("WorkflowEngine", "Scenario Created", $"Створено сценарій '{rule.Name}' ({rule.Condition.Describe()} → {rule.Action.Describe()})");
        NotifyChanged();
        return rule;
    }

    public bool RemoveRule(string id)
    {
        bool removed;
        lock (_syncLock)
        {
            var rule = _rules.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            removed = rule is not null && _rules.Remove(rule);
        }
        if (removed) NotifyChanged();
        return removed;
    }

    public void ToggleRule(string id)
    {
        lock (_syncLock)
        {
            var rule = _rules.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (rule is not null)
            {
                rule.IsEnabled = !rule.IsEnabled;
            }
        }
        NotifyChanged();
    }

    private async Task HandleDevicePolledAsync(MonitoredDeviceState device, bool statusChanged, bool becameOnline)
    {
        List<WorkflowRule> activeRules;
        lock (_syncLock)
        {
            activeRules = _rules.Where(r => r.IsEnabled).ToList();
        }

        foreach (var rule in activeRules)
        {
            if (!MatchesDeviceFilter(rule.TriggerDeviceFilter, device))
                continue;

            bool triggerMatched = rule.TriggerType switch
            {
                WorkflowTriggerType.TelemetryUpdate => device.IsOnline,
                WorkflowTriggerType.DeviceBecameOnline => statusChanged && becameOnline && device.IsOnline,
                WorkflowTriggerType.DeviceBecameOffline => statusChanged && !device.IsOnline,
                WorkflowTriggerType.ManualOnly => false,
                _ => false
            };

            if (!triggerMatched) continue;

            // Check cooldown
            if (rule.LastTriggeredLocal.HasValue &&
                (DateTime.Now - rule.LastTriggeredLocal.Value).TotalSeconds < Math.Max(1, rule.CooldownSeconds))
            {
                continue;
            }

            if (!EvaluateCondition(rule.Condition, device))
                continue;

            await ExecuteRuleActionAsync(rule, device, $"Авто-тригер: {device.EffectiveName} ({rule.Condition.Describe()})");
        }
    }

    public async Task<(bool Success, string Output)> TriggerRuleManuallyAsync(string idOrName)
    {
        WorkflowRule? rule;
        lock (_syncLock)
        {
            rule = _rules.FirstOrDefault(r =>
                string.Equals(r.Id, idOrName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Name, idOrName, StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains(idOrName, StringComparison.OrdinalIgnoreCase));
        }

        if (rule is null)
        {
            return (false, $"Сценарій '{idOrName}' не знайдено.");
        }

        var contextDevice = _monitor.FindDevice(rule.Action.TargetDeviceNameOrUrl)
                            ?? _monitor.FindDevice(rule.TriggerDeviceFilter)
                            ?? _monitor.GetDevices().FirstOrDefault();

        var result = await ExecuteRuleActionAsync(rule, contextDevice, "Ручний / AI запуск");
        return (!rule.LastExecutionError, result);
    }

    private async Task<string> ExecuteRuleActionAsync(WorkflowRule rule, MonitoredDeviceState? contextDevice, string triggerReason)
    {
        string output;
        bool isError = false;

        try
        {
            if (rule.Action.ActionType == WorkflowActionType.DashboardNotification)
            {
                var msg = !string.IsNullOrWhiteSpace(rule.Action.NotificationMessage)
                    ? rule.Action.NotificationMessage!
                    : $"Спрацював сценарій '{rule.Name}' ({triggerReason})";
                output = msg;
                _monitor.AddExternalLog("WorkflowEngine", $"Alert [{rule.Name}]", msg);
            }
            else
            {
                var targetSpec = rule.Action.TargetDeviceNameOrUrl == "*" && contextDevice is not null
                    ? contextDevice.EffectiveName
                    : rule.Action.TargetDeviceNameOrUrl;

                var targetDevice = _monitor.FindDevice(targetSpec) ?? contextDevice;
                if (targetDevice is null)
                {
                    output = $"Цільовий пристрій '{targetSpec}' не знайдено.";
                    isError = true;
                }
                else if (!targetDevice.IsOnline)
                {
                    output = $"Цільовий пристрій '{targetDevice.EffectiveName}' зараз OFFLINE.";
                    isError = true;
                }
                else
                {
                    var cmdName = rule.Action.CommandOrActionName.Trim();
                    var matchedAction = targetDevice.Actions.FirstOrDefault(a =>
                        string.Equals(a.Action, cmdName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a.Command, cmdName, StringComparison.OrdinalIgnoreCase));

                    matchedAction ??= new DeviceAction(
                        cmdName,
                        cmdName.Equals("info", StringComparison.OrdinalIgnoreCase) ? CommandType.Get : CommandType.Post,
                        cmdName.ToLowerInvariant());

                    output = await _monitor.ExecuteActionAsync(targetDevice, matchedAction);
                    isError = targetDevice.LastActionIsError;
                }
            }
        }
        catch (Exception ex)
        {
            output = $"Помилка сценарію: {ex.Message}";
            isError = true;
        }

        lock (_syncLock)
        {
            rule.LastTriggeredLocal = DateTime.Now;
            rule.ExecutionCount++;
            rule.LastExecutionResult = output;
            rule.LastExecutionError = isError;

            _history.Insert(0, new WorkflowExecutionLog(
                DateTime.Now,
                rule.Id,
                rule.Name,
                triggerReason,
                rule.Action.Describe(),
                output,
                isError));

            if (_history.Count > 40)
            {
                _history.RemoveAt(_history.Count - 1);
            }
        }

        NotifyChanged();
        return output;
    }

    private static bool MatchesDeviceFilter(string filter, MonitoredDeviceState device)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Trim() == "*")
            return true;

        var f = filter.Trim();
        return string.Equals(device.EffectiveName, f, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(device.ReportedName, f, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(device.ConfiguredName, f, StringComparison.OrdinalIgnoreCase) ||
               device.EndpointUrl.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private bool EvaluateCondition(WorkflowCondition condition, MonitoredDeviceState device)
    {
        if (condition.Field == WorkflowMetricField.IsOnline)
        {
            var targetBool = !string.Equals(condition.TargetValue?.Trim(), "false", StringComparison.OrdinalIgnoreCase) &&
                             condition.TargetValue?.Trim() != "0";
            return condition.Operator switch
            {
                ComparisonOperator.NotEquals => device.IsOnline != targetBool,
                _ => device.IsOnline == targetBool
            };
        }

        double actualValue = condition.Field switch
        {
            WorkflowMetricField.TemperatureCelsius => device.TemperatureCelsius,
            WorkflowMetricField.UptimeSeconds => device.UptimeSeconds,
            WorkflowMetricField.ResponseTimeMs => device.ResponseTimeMs,
            WorkflowMetricField.OnlineDevicesCount => _monitor.OnlineCount,
            _ => 0
        };

        if (!double.TryParse(condition.TargetValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var threshold))
        {
            return false;
        }

        return condition.Operator switch
        {
            ComparisonOperator.GreaterThan => actualValue > threshold,
            ComparisonOperator.GreaterThanOrEqual => actualValue >= threshold,
            ComparisonOperator.LessThan => actualValue < threshold,
            ComparisonOperator.LessThanOrEqual => actualValue <= threshold,
            ComparisonOperator.Equals => Math.Abs(actualValue - threshold) < 0.001,
            ComparisonOperator.NotEquals => Math.Abs(actualValue - threshold) >= 0.001,
            _ => false
        };
    }

    private void NotifyChanged()
    {
        try
        {
            OnWorkflowStateChanged?.Invoke();
        }
        catch
        {
            // Ignore UI listener exceptions
        }
    }

    public void Dispose()
    {
        _monitor.OnDevicePolledAsync -= HandleDevicePolledAsync;
    }
}
