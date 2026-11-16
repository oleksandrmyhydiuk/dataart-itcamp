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

    public static double GetHumidity()
    {
        try
        {
            if (File.Exists("/sys/bus/iio/devices/iio:device0/in_humidityrelative_input"))
            {
                var humidityText = File.ReadAllText("/sys/bus/iio/devices/iio:device0/in_humidityrelative_input");
                return Math.Round(double.Parse(humidityText, CultureInfo.InvariantCulture) / 1000.0, 1);
            }
        }
        catch
        {
            // Fallback to simulated sensor reading below
        }

        return Math.Round(50.0 + Math.Cos(Environment.TickCount64 / 12000.0) * 7.5 + Random.Shared.NextDouble() * 2.0, 1);
    }

    public static double GetPowerConsumptionWatts()
    {
        try
        {
            if (File.Exists("/sys/class/power_supply/BAT0/power_now"))
            {
                var powerMicroWatts = double.Parse(File.ReadAllText("/sys/class/power_supply/BAT0/power_now"), CultureInfo.InvariantCulture);
                return Math.Round(powerMicroWatts / 1_000_000.0, 1);
            }
        }
        catch
        {
            // Fallback to simulated smart plug / device power sensor
        }

        return Math.Round(115.0 + Math.Sin(Environment.TickCount64 / 8000.0) * 28.0 + Random.Shared.NextDouble() * 6.0, 1);
    }

    public static int GetCo2Ppm()
    {
        return (int)Math.Round(490.0 + Math.Sin(Environment.TickCount64 / 15000.0) * 65.0 + Random.Shared.Next(-12, 15));
    }
}
