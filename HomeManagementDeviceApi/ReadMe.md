# HomeManagementDeviceApi & Monitoring Dashboard

## 1. Run emulated devices (`HomeManagementDeviceApi`)
Run the next commands in separate terminals inside `HomeManagementDeviceApi`:
```cmd
dotnet run --launch-profile Device1

dotnet run --launch-profile Device2
```

Open `http://localhost:1235/info` and `http://localhost:1237/info`. You should see the JSON output.

## 2. Run the Monitoring Dashboard (`HomeManagementDashboard`)
```cmd
cd ..\HomeManagementDashboard
dotnet run
```

Open `http://localhost:5180` in your browser to view the live **Device Monitoring Dashboard** (showing online/offline device counts, real-time temperature charts, device status, uptime, and remote command execution).
