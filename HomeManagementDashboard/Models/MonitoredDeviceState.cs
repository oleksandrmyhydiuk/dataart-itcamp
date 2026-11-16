using HomeManagement.Shared;

namespace HomeManagementDashboard.Models;

public record DeviceEndpointConfig
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public record MetricSample(DateTime Timestamp, double Value);

public record ActivityLogEntry(
    DateTime Timestamp,
    string DeviceName,
    string Category,
    string Message,
    bool IsError = false);

public class MonitoredDeviceState
{
    public required string Id { get; init; }
    public required string EndpointUrl { get; set; }
    public string ConfiguredName { get; set; } = string.Empty;
    public string? ReportedName { get; set; }
    public string? ReportedAddress { get; set; }
    public bool IsOnline { get; set; }
    public bool IsChecking { get; set; }
    public bool HasEverBeenChecked { get; set; }
    public DateTime? LastCheckedLocal { get; set; }
    public DateTime? LastOnlineLocal { get; set; }
    public long ResponseTimeMs { get; set; }
    public string? ErrorMessage { get; set; }
    public int UptimeSeconds { get; set; }
    public double TemperatureCelsius { get; set; }
    public double HumidityPercent { get; set; }
    public double PowerWatts { get; set; }
    public int Co2Ppm { get; set; }
    public Dictionary<string, string> RawMetrics { get; set; } = new();
    public List<DeviceAction> Actions { get; set; } = new();
    public List<MetricSample> TemperatureHistory { get; set; } = new();
    public List<MetricSample> HumidityHistory { get; set; } = new();
    public List<MetricSample> PowerHistory { get; set; } = new();
    public List<MetricSample> Co2History { get; set; } = new();
    public string? LastActionOutput { get; set; }
    public string? LastActionName { get; set; }
    public DateTime? LastActionTime { get; set; }
    public bool LastActionIsError { get; set; }

    public string EffectiveName =>
        !string.IsNullOrWhiteSpace(ReportedName)
            ? ReportedName!
            : !string.IsNullOrWhiteSpace(ConfiguredName)
                ? ConfiguredName
                : EndpointUrl;

    public string FormattedUptime
    {
        get
        {
            if (UptimeSeconds <= 0) return "0s";
            var ts = TimeSpan.FromSeconds(UptimeSeconds);
            if (ts.TotalDays >= 1)
                return $"{(int)ts.TotalDays}d {ts.Hours:D2}h {ts.Minutes:D2}m";
            if (ts.TotalHours >= 1)
                return $"{ts.Hours:D2}h {ts.Minutes:D2}m {ts.Seconds:D2}s";
            return $"{ts.Minutes}m {ts.Seconds:D2}s";
        }
    }
}
