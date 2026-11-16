using HomeManagementDashboard.Models;
using HomeManagementDashboard.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeManagement.Tests;

public class DashboardMonitorTests
{
    private static DeviceMonitorService CreateService()
    {
        var configData = new Dictionary<string, string?>
        {
            ["MonitoredDevices:0:Name"] = "Device1",
            ["MonitoredDevices:0:Url"] = "http://localhost:1235",
            ["MonitoredDevices:1:Name"] = "Device2",
            ["MonitoredDevices:1:Url"] = "http://localhost:1237"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        return new DeviceMonitorService(configuration, NullLogger<DeviceMonitorService>.Instance);
    }

    [Fact]
    public void Constructor_LoadsConfiguredDevices()
    {
        using var service = CreateService();
        var devices = service.GetDevices();

        Assert.Equal(2, service.TotalCount);
        Assert.Equal("Device1", devices[0].EffectiveName);
        Assert.Equal("http://localhost:1235", devices[0].EndpointUrl);
        Assert.Equal("Device2", devices[1].EffectiveName);
        Assert.Equal("http://localhost:1237", devices[1].EndpointUrl);
    }

    [Fact]
    public void FindDevice_LocatesByNameAndByUrl()
    {
        using var service = CreateService();

        var byName = service.FindDevice("Device1");
        var byUrl = service.FindDevice("http://localhost:1237/");
        var missing = service.FindDevice("NonExistentDevice99");

        Assert.NotNull(byName);
        Assert.Equal("http://localhost:1235", byName!.EndpointUrl);
        Assert.NotNull(byUrl);
        Assert.Equal("Device2", byUrl!.EffectiveName);
        Assert.Null(missing);
    }

    [Fact]
    public void RemoveDevice_DecrementsTotalCount()
    {
        using var service = CreateService();
        var firstId = service.GetDevices().First().Id;

        service.RemoveDevice(firstId);

        Assert.Equal(1, service.TotalCount);
    }

    [Fact]
    public void ExternalLogs_CanBeAddedAndCleared()
    {
        using var service = CreateService();

        service.AddExternalLog("TestSource", "UnitTest", "Test log entry");
        Assert.NotEmpty(service.GetActivityLogs());
        Assert.Equal("Test log entry", service.GetActivityLogs()[0].Message);

        service.ClearLogs();
        Assert.Empty(service.GetActivityLogs());
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(125, "2m 05s")]
    [InlineData(3665, "01h 01m 05s")]
    [InlineData(90061, "1d 01h 01m")]
    public void MonitoredDeviceState_FormatsUptimeCorrectly(int seconds, string expected)
    {
        var state = new MonitoredDeviceState
        {
            Id = "dev-1",
            EndpointUrl = "http://localhost:1235",
            UptimeSeconds = seconds
        };

        Assert.Equal(expected, state.FormattedUptime);
    }
}
