using HomeManagementDashboard.Models;
using HomeManagementDashboard.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeManagement.Tests;

public class WorkflowEngineTests
{
    private static (DeviceMonitorService Monitor, WorkflowEngineService Engine) CreateEngine()
    {
        var configuration = new ConfigurationBuilder().Build();
        var monitor = new DeviceMonitorService(configuration, NullLogger<DeviceMonitorService>.Instance);
        var engine = new WorkflowEngineService(monitor);
        return (monitor, engine);
    }

    [Fact]
    public void SeedDefaultScenarios_CreatesInitialWorkflows()
    {
        var (monitor, engine) = CreateEngine();
        using var _ = monitor;
        using var __ = engine;

        var rules = engine.GetRules();
        Assert.True(rules.Count >= 4);
        Assert.Contains(rules, r => r.Id == "wf-temp-guard");
        Assert.Contains(rules, r => r.Id == "wf-co2-ventilation");
    }

    [Fact]
    public void AddToggleAndRemoveRule_ModifiesRuleCollection()
    {
        var (monitor, engine) = CreateEngine();
        using var _ = monitor;
        using var __ = engine;

        var customRule = engine.AddRule(new WorkflowRule
        {
            Id = "wf-custom-humidity",
            Name = "High Humidity Alert",
            IsEnabled = true,
            TriggerType = WorkflowTriggerType.TelemetryUpdate,
            TriggerDeviceFilter = "Device1",
            Condition = new WorkflowCondition
            {
                Field = WorkflowMetricField.HumidityPercent,
                Operator = ComparisonOperator.GreaterThan,
                TargetValue = "60.0"
            },
            Action = new WorkflowActionDefinition
            {
                ActionType = WorkflowActionType.DashboardNotification,
                NotificationMessage = "Humidity exceeded 60%"
            }
        });

        Assert.Contains(engine.GetRules(), r => r.Id == customRule.Id && r.IsEnabled);

        engine.ToggleRule(customRule.Id);
        Assert.Contains(engine.GetRules(), r => r.Id == customRule.Id && !r.IsEnabled);

        var removed = engine.RemoveRule(customRule.Id);
        Assert.True(removed);
        Assert.DoesNotContain(engine.GetRules(), r => r.Id == customRule.Id);
    }

    [Fact]
    public async Task TriggerRuleManuallyAsync_ExecutesNotificationScenarioAndLogsHistory()
    {
        var (monitor, engine) = CreateEngine();
        using var _ = monitor;
        using var __ = engine;

        var (success, output) = await engine.TriggerRuleManuallyAsync("wf-co2-ventilation");

        Assert.True(success);
        Assert.Contains("CO₂", output);
        Assert.NotEmpty(engine.GetExecutionHistory());
        Assert.Equal("wf-co2-ventilation", engine.GetExecutionHistory()[0].RuleId);
    }

    [Fact]
    public void WorkflowCondition_Describe_FormatsHumanReadableExpression()
    {
        var cond = new WorkflowCondition
        {
            Field = WorkflowMetricField.PowerWatts,
            Operator = ComparisonOperator.GreaterThanOrEqual,
            TargetValue = "150"
        };

        Assert.Equal("Power (W) >= 150", cond.Describe());
    }
}
