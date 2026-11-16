namespace HomeManagementDashboard.Models;

public enum WorkflowTriggerType
{
    TelemetryUpdate,
    DeviceBecameOnline,
    DeviceBecameOffline,
    ManualOnly
}

public enum WorkflowMetricField
{
    TemperatureCelsius,
    HumidityPercent,
    PowerWatts,
    Co2Ppm,
    UptimeSeconds,
    ResponseTimeMs,
    IsOnline,
    OnlineDevicesCount
}

public enum ComparisonOperator
{
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Equals,
    NotEquals
}

public enum WorkflowActionType
{
    ExecuteDeviceAction,
    DashboardNotification
}

public class WorkflowCondition
{
    public WorkflowMetricField Field { get; set; } = WorkflowMetricField.TemperatureCelsius;
    public ComparisonOperator Operator { get; set; } = ComparisonOperator.GreaterThan;
    public string TargetValue { get; set; } = "44.0";

    public string Describe()
    {
        var fieldName = Field switch
        {
            WorkflowMetricField.TemperatureCelsius => "Temperature (°C)",
            WorkflowMetricField.HumidityPercent => "Humidity (%)",
            WorkflowMetricField.PowerWatts => "Power (W)",
            WorkflowMetricField.Co2Ppm => "CO₂ (ppm)",
            WorkflowMetricField.UptimeSeconds => "Uptime (сек)",
            WorkflowMetricField.ResponseTimeMs => "Ping (мс)",
            WorkflowMetricField.IsOnline => "IsOnline",
            WorkflowMetricField.OnlineDevicesCount => "Online Devices Count",
            _ => Field.ToString()
        };

        var opSymbol = Operator switch
        {
            ComparisonOperator.GreaterThan => ">",
            ComparisonOperator.GreaterThanOrEqual => ">=",
            ComparisonOperator.LessThan => "<",
            ComparisonOperator.LessThanOrEqual => "<=",
            ComparisonOperator.Equals => "==",
            ComparisonOperator.NotEquals => "!=",
            _ => "=="
        };

        return $"{fieldName} {opSymbol} {TargetValue}";
    }
}

public class WorkflowActionDefinition
{
    public WorkflowActionType ActionType { get; set; } = WorkflowActionType.ExecuteDeviceAction;
    public string TargetDeviceNameOrUrl { get; set; } = "Device1";
    public string CommandOrActionName { get; set; } = "INFO";
    public string? NotificationMessage { get; set; }

    public string Describe()
    {
        return ActionType switch
        {
            WorkflowActionType.ExecuteDeviceAction => $"Виконати '{CommandOrActionName}' на {TargetDeviceNameOrUrl}",
            WorkflowActionType.DashboardNotification => $"Сповіщення: \"{NotificationMessage ?? CommandOrActionName}\"",
            _ => ActionType.ToString()
        };
    }
}

public class WorkflowRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    public WorkflowTriggerType TriggerType { get; set; } = WorkflowTriggerType.TelemetryUpdate;
    public string TriggerDeviceFilter { get; set; } = "Device1"; // "*" for any device

    public WorkflowCondition Condition { get; set; } = new();
    public WorkflowActionDefinition Action { get; set; } = new();

    public int CooldownSeconds { get; set; } = 30;
    public DateTime? LastTriggeredLocal { get; set; }
    public int ExecutionCount { get; set; }
    public string? LastExecutionResult { get; set; }
    public bool LastExecutionError { get; set; }

    public string DescribeTrigger()
    {
        var dev = string.IsNullOrWhiteSpace(TriggerDeviceFilter) || TriggerDeviceFilter == "*"
            ? "будь-який пристрій"
            : TriggerDeviceFilter;

        return TriggerType switch
        {
            WorkflowTriggerType.TelemetryUpdate => $"Оновлення телеметрії ({dev})",
            WorkflowTriggerType.DeviceBecameOnline => $"Пристрій з'явився в мережі ({dev})",
            WorkflowTriggerType.DeviceBecameOffline => $"Пристрій втратив зв'язок ({dev})",
            WorkflowTriggerType.ManualOnly => "Лише ручний або AI-запуск",
            _ => TriggerType.ToString()
        };
    }
}

public record WorkflowExecutionLog(
    DateTime Timestamp,
    string RuleId,
    string RuleName,
    string TriggerReason,
    string ActionSummary,
    string ResultOutput,
    bool IsError = false);
