using HomeManagement.Shared;
using HomeManagementDeviceApi;
using Xunit;

namespace HomeManagement.Tests;

public class DeviceApiTelemetryTests
{
    [Fact]
    public void GetUptime_ReturnsNonNegativeSeconds()
    {
        var uptime = DeviceManager.GetUptime();
        Assert.True(uptime >= 0, $"Expected non-negative uptime, but got {uptime}");
    }

    [Fact]
    public void GetTemperature_ReturnsRealisticRange()
    {
        var temp = DeviceManager.GetTemperature();
        Assert.InRange(temp, -20.0, 110.0);
    }

    [Fact]
    public void GetHumidity_ReturnsValidPercentage()
    {
        var humidity = DeviceManager.GetHumidity();
        Assert.InRange(humidity, 0.0, 100.0);
    }

    [Fact]
    public void GetPowerConsumptionWatts_ReturnsPositiveLoad()
    {
        var watts = DeviceManager.GetPowerConsumptionWatts();
        Assert.InRange(watts, 1.0, 5000.0);
    }

    [Fact]
    public void GetCo2Ppm_ReturnsRealisticIndoorConcentration()
    {
        var co2 = DeviceManager.GetCo2Ppm();
        Assert.InRange(co2, 300, 5000);
    }

    [Fact]
    public void NetworkManager_ReturnsFormattedIpAndAddress()
    {
        var ip = NetworkManager.GetLocalIp();
        var subnet = NetworkManager.GetLocalSubnet();
        var address = NetworkManager.GetAddress();

        Assert.False(string.IsNullOrWhiteSpace(ip));
        Assert.Equal(4, ip.Split('.').Length);
        Assert.EndsWith(".", subnet);
        Assert.StartsWith("http://", address);
    }

    [Fact]
    public void DeviceModels_InitializePropertiesCorrectly()
    {
        var info = new DeviceInfo
        {
            Name = "Device1",
            Address = "http://192.168.1.10",
            Metrics = new Dictionary<string, string>
            {
                ["Temperature"] = "43.5",
                ["Humidity"] = "51.2"
            }
        };

        var action = new DeviceAction("Start Notepad", CommandType.Post, "command", "{\"FileName\":\"notepad\"}");
        var config = new DeviceConfig
        {
            Name = "Device1",
            Address = "http://192.168.1.10",
            Actions = [action]
        };
        var cmd = new Command("git") { Arguments = ["--status"] };

        Assert.Equal("Device1", info.Name);
        Assert.Equal("43.5", info.Metrics["Temperature"]);
        Assert.Single(config.Actions);
        Assert.Equal(CommandType.Post, config.Actions[0].CommandType);
        Assert.Equal("git", cmd.FileName);
        Assert.Single(cmd.Arguments);
    }
}
