using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomeManagement.Shared;
using HomeManagementDashboard.Models;

namespace HomeManagementDashboard.Services;

public class DeviceMonitorService : IHostedService, IDisposable
{
    private readonly ILogger<DeviceMonitorService> _logger;
    private readonly HttpClient _httpClient;
    private readonly List<MonitoredDeviceState> _devices = new();
    private readonly List<ActivityLogEntry> _activityLogs = new();
    private readonly Lock _syncLock = new();
    private PeriodicTimer? _timer;
    private Task? _pollingTask;
    private CancellationTokenSource? _cts;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public bool AutoRefreshEnabled { get; set; } = true;
    public int RefreshIntervalSeconds { get; set; } = 3;
    public DateTime? LastGlobalRefresh { get; private set; }

    public event Action? OnStateChanged;
    public event Func<MonitoredDeviceState, bool, bool, Task>? OnDevicePolledAsync;

    public DeviceMonitorService(IConfiguration configuration, ILogger<DeviceMonitorService> logger)
    {
        _logger = logger;

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AllowAutoRedirect = true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(3.5)
        };

        var configured = configuration.GetSection("MonitoredDevices").Get<List<DeviceEndpointConfig>>() ?? new();
        if (configured.Count == 0)
        {
            configured.Add(new DeviceEndpointConfig { Name = "Device1", Url = "http://localhost:1235" });
            configured.Add(new DeviceEndpointConfig { Name = "Device2", Url = "http://localhost:1237" });
        }

        foreach (var item in configured)
        {
            AddDeviceInternal(item.Url, item.Name);
        }
    }

    public IReadOnlyList<MonitoredDeviceState> GetDevices()
    {
        lock (_syncLock)
        {
            return _devices.ToList();
        }
    }

    public IReadOnlyList<ActivityLogEntry> GetActivityLogs()
    {
        lock (_syncLock)
        {
            return _activityLogs.ToList();
        }
    }

    public int TotalCount
    {
        get
        {
            lock (_syncLock) return _devices.Count;
        }
    }

    public int OnlineCount
    {
        get
        {
            lock (_syncLock) return _devices.Count(d => d.IsOnline);
        }
    }

    public int OfflineCount
    {
        get
        {
            lock (_syncLock) return _devices.Count(d => !d.IsOnline);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(RefreshIntervalSeconds));
        _pollingTask = RunPollingLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunPollingLoopAsync(CancellationToken token)
    {
        try
        {
            await RefreshAllAsync(token);

            while (_timer is not null && await _timer.WaitForNextTickAsync(token))
            {
                if (AutoRefreshEnabled)
                {
                    await RefreshAllAsync(token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in device polling loop");
        }
    }

    public async Task RefreshAllAsync(CancellationToken token = default)
    {
        List<MonitoredDeviceState> snapshot;
        lock (_syncLock)
        {
            snapshot = _devices.ToList();
        }

        await Task.WhenAll(snapshot.Select(d => PollDeviceAsync(d, token)));
        LastGlobalRefresh = DateTime.Now;
        NotifyStateChanged();
    }

    public async Task PollDeviceAsync(MonitoredDeviceState device, CancellationToken token = default)
    {
        var wasOnline = device.IsOnline;
        var hadBeenChecked = device.HasEverBeenChecked;
        device.IsChecking = true;
        NotifyStateChanged();

        var sw = Stopwatch.StartNew();
        try
        {
            var baseUrl = device.EndpointUrl.TrimEnd('/');
            using var infoResponse = await _httpClient.GetAsync($"{baseUrl}/info", token);
            infoResponse.EnsureSuccessStatusCode();

            var info = await infoResponse.Content.ReadFromJsonAsync<DeviceInfo>(JsonOptions, token);
            sw.Stop();

            if (info is null)
            {
                throw new InvalidOperationException("Empty response from /info endpoint.");
            }

            // Also fetch /config if actions are empty or periodically
            try
            {
                using var configResponse = await _httpClient.GetAsync($"{baseUrl}/config", token);
                if (configResponse.IsSuccessStatusCode)
                {
                    var config = await configResponse.Content.ReadFromJsonAsync<DeviceConfig>(JsonOptions, token);
                    if (config?.Actions is not null)
                    {
                        device.Actions = config.Actions.ToList();
                    }
                }
            }
            catch
            {
                // Keep previous actions if /config fails transiently
            }

            lock (_syncLock)
            {
                device.ReportedName = info.Name;
                device.ReportedAddress = info.Address;
                device.RawMetrics = info.Metrics ?? new Dictionary<string, string>();
                device.ResponseTimeMs = sw.ElapsedMilliseconds;
                device.IsOnline = true;
                device.ErrorMessage = null;
                device.LastCheckedLocal = DateTime.Now;
                device.LastOnlineLocal = DateTime.Now;
                device.HasEverBeenChecked = true;

                if (device.RawMetrics.TryGetValue("UptimeSeconds", out var uptimeStr) &&
                    int.TryParse(uptimeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var uptime))
                {
                    device.UptimeSeconds = uptime;
                }

                if (device.RawMetrics.TryGetValue("Temperature", out var tempStr) &&
                    double.TryParse(tempStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var temp))
                {
                    device.TemperatureCelsius = Math.Round(temp, 1);
                    device.TemperatureHistory.Add(new MetricSample(DateTime.Now, device.TemperatureCelsius));
                    if (device.TemperatureHistory.Count > 24)
                    {
                        device.TemperatureHistory.RemoveAt(0);
                    }
                }

                if (!wasOnline)
                {
                    AddLogInternal(device.EffectiveName, "Status", $"Пристрій в мережі (ONLINE) — {device.EndpointUrl} ({device.ResponseTimeMs} мс)");
                }
            }

            if (OnDevicePolledAsync is not null)
            {
                await OnDevicePolledAsync.Invoke(device, !wasOnline || !hadBeenChecked, !wasOnline);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            sw.Stop();
            var statusChanged = wasOnline || !hadBeenChecked;
            lock (_syncLock)
            {
                device.IsOnline = false;
                device.ResponseTimeMs = 0;
                device.LastCheckedLocal = DateTime.Now;
                device.ErrorMessage = ex is TaskCanceledException
                    ? "Таймаут з'єднання (пристрій не відповідає)"
                    : "Не вдалося підключитися до пристрою";

                if (statusChanged)
                {
                    AddLogInternal(device.EffectiveName, "Status", $"Пристрій недоступний (OFFLINE) — {device.EndpointUrl}", isError: true);
                }
                device.HasEverBeenChecked = true;
            }

            if (OnDevicePolledAsync is not null)
            {
                await OnDevicePolledAsync.Invoke(device, statusChanged, false);
            }
        }
        finally
        {
            device.IsChecking = false;
            NotifyStateChanged();
        }
    }

    public async Task<string> ExecuteActionAsync(MonitoredDeviceState device, DeviceAction action)
    {
        var baseUrl = device.EndpointUrl.TrimEnd('/');
        var targetUrl = $"{baseUrl}/{action.Command.TrimStart('/')}";

        try
        {
            HttpResponseMessage response;
            if (action.CommandType == CommandType.Get)
            {
                response = await _httpClient.GetAsync(targetUrl);
            }
            else
            {
                var content = !string.IsNullOrWhiteSpace(action.CommandArgs)
                    ? new StringContent(action.CommandArgs, Encoding.UTF8, "application/json")
                    : new StringContent(string.Empty, Encoding.UTF8, "application/json");
                response = await _httpClient.PostAsync(targetUrl, content);
            }

            var body = await response.Content.ReadAsStringAsync();
            var output = string.IsNullOrWhiteSpace(body)
                ? $"Виконано успішно (HTTP {(int)response.StatusCode})"
                : body.Trim();

            lock (_syncLock)
            {
                device.LastActionName = action.Action;
                device.LastActionOutput = output;
                device.LastActionTime = DateTime.Now;
                device.LastActionIsError = !response.IsSuccessStatusCode;
                AddLogInternal(device.EffectiveName, $"Action [{action.Action}]", output, !response.IsSuccessStatusCode);
            }

            NotifyStateChanged();
            return output;
        }
        catch (Exception ex)
        {
            var errMsg = $"Помилка виконання '{action.Action}': {ex.Message}";
            lock (_syncLock)
            {
                device.LastActionName = action.Action;
                device.LastActionOutput = errMsg;
                device.LastActionTime = DateTime.Now;
                device.LastActionIsError = true;
                AddLogInternal(device.EffectiveName, $"Action [{action.Action}]", errMsg, isError: true);
            }

            NotifyStateChanged();
            return errMsg;
        }
    }

    public async Task<bool> AddDeviceAsync(string url, string? customName = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        var normalized = url.Trim().TrimEnd('/');
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"http://{normalized}";
        }

        MonitoredDeviceState? added;
        lock (_syncLock)
        {
            if (_devices.Any(d => string.Equals(d.EndpointUrl.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            added = AddDeviceInternal(normalized, customName ?? normalized);
            AddLogInternal(added.EffectiveName, "System", $"Додано новий пристрій для моніторингу: {normalized}");
        }

        if (added is not null)
        {
            await PollDeviceAsync(added);
        }

        return true;
    }

    public void RemoveDevice(string id)
    {
        lock (_syncLock)
        {
            var target = _devices.FirstOrDefault(d => d.Id == id);
            if (target is not null)
            {
                _devices.Remove(target);
                AddLogInternal(target.EffectiveName, "System", $"Пристрій видалено зі списку моніторингу ({target.EndpointUrl})");
            }
        }
        NotifyStateChanged();
    }

    public async Task<int> DiscoverLocalhostDevicesAsync()
    {
        var candidatePorts = new[] { 1234, 1235, 1236, 1237, 1238, 1239, 5000, 5001 };
        int discovered = 0;

        foreach (var port in candidatePorts)
        {
            var url = $"http://localhost:{port}";
            bool alreadyExists;
            lock (_syncLock)
            {
                alreadyExists = _devices.Any(d =>
                    d.EndpointUrl.EndsWith($":{port}", StringComparison.OrdinalIgnoreCase) ||
                    (port == 1234 && d.EndpointUrl.EndsWith(":1235")) ||
                    (port == 1236 && d.EndpointUrl.EndsWith(":1237")));
            }

            if (alreadyExists) continue;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                using var resp = await _httpClient.GetAsync($"{url}/info", cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var info = await resp.Content.ReadFromJsonAsync<DeviceInfo>(JsonOptions, cts.Token);
                    if (info is not null && !string.IsNullOrWhiteSpace(info.Name))
                    {
                        await AddDeviceAsync(url, info.Name);
                        discovered++;
                    }
                }
            }
            catch
            {
                // Port not running a device
            }
        }

        return discovered;
    }

    public void ClearLogs()
    {
        lock (_syncLock)
        {
            _activityLogs.Clear();
        }
        NotifyStateChanged();
    }

    public void AddExternalLog(string deviceOrSource, string category, string message, bool isError = false)
    {
        lock (_syncLock)
        {
            AddLogInternal(deviceOrSource, category, message, isError);
        }
        NotifyStateChanged();
    }

    public MonitoredDeviceState? FindDevice(string nameOrUrl)
    {
        if (string.IsNullOrWhiteSpace(nameOrUrl)) return null;
        var q = nameOrUrl.Trim().TrimEnd('/');
        lock (_syncLock)
        {
            return _devices.FirstOrDefault(d =>
                string.Equals(d.Id, q, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.EffectiveName, q, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.ReportedName, q, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.ConfiguredName, q, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.EndpointUrl.TrimEnd('/'), q, StringComparison.OrdinalIgnoreCase) ||
                d.EffectiveName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.EndpointUrl.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
    }

    private MonitoredDeviceState AddDeviceInternal(string url, string name)
    {
        var state = new MonitoredDeviceState
        {
            Id = Guid.NewGuid().ToString("N"),
            EndpointUrl = url.TrimEnd('/'),
            ConfiguredName = name
        };
        _devices.Add(state);
        return state;
    }

    private void AddLogInternal(string deviceName, string category, string message, bool isError = false)
    {
        _activityLogs.Insert(0, new ActivityLogEntry(DateTime.Now, deviceName, category, message, isError));
        if (_activityLogs.Count > 50)
        {
            _activityLogs.RemoveAt(_activityLogs.Count - 1);
        }
    }

    private void NotifyStateChanged()
    {
        try
        {
            OnStateChanged?.Invoke();
        }
        catch
        {
            // Ignore UI subscriber exceptions
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }
        _timer?.Dispose();
        if (_pollingTask is not null)
        {
            await Task.WhenAny(_pollingTask, Task.Delay(1000, cancellationToken));
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _timer?.Dispose();
        _httpClient.Dispose();
    }
}
