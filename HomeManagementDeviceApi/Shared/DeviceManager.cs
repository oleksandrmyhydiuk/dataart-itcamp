using System.Globalization;

namespace HomeManagement.Shared;

public static class DeviceManager
{
    public static int GetUptime()
    {
        try
        {
            var uptimeText = File.ReadAllText("/proc/uptime");
            var uptimeSeconds = double.Parse(uptimeText.Split(' ')[0], CultureInfo.InvariantCulture);

            return (int)uptimeSeconds;
        }
        catch
        {
            return (int)(Environment.TickCount64 / 1000);
        }
    }

    public static double GetTemperature()
    {
        try
        {
            var tempText = File.ReadAllText("/sys/class/thermal/thermal_zone0/temp");
            var tempCelsius = double.Parse(tempText, CultureInfo.InvariantCulture) / 1000.0;

            return tempCelsius;
        }
        catch
        {
            return Math.Round(42.0 + Math.Sin(Environment.TickCount64 / 10000.0) * 4.0 + Random.Shared.NextDouble() * 1.5, 1);
        }
    }
}