# Security Policy

## Supported Versions

The following versions of the **HomeManagement** platform are currently supported with security updates:

| Version | .NET Target | Supported |
| :--- | :--- | :--- |
| `1.0.x` | `.NET 11.0` | :white_check_mark: Yes |
| `< 1.0` | `.NET 10.0` or older | :x: No |

---

## Security Model & Intended Environment

This repository was created as an educational and prototyping project for the **DataArt IT Camp** to demonstrate smart home device monitoring, workflow automation, and AI Model Context Protocol (MCP) integration.

### Important Operational Considerations

1. **Remote Command Execution (`POST /command`, `/restart`, `/shutdown`)**
   - `HomeManagementDeviceApi` intentionally exposes endpoints that execute system commands configured in `appsettings.<Environment>.json` (such as `notepad`, `calc`, `mspaint`, `git --status`, `reboot`, `poweroff`).
   - **Do not expose `HomeManagementDeviceApi` or `HomeManagementDashboard` directly to the public internet.** Run these services strictly on `localhost` or within an isolated, trusted local area network (LAN).

2. **Development HTTPS Certificates**
   - In development mode, `DeviceMonitorService` and `HomeManagementMcpServer` allow local self-signed ASP.NET Core developer certificates to facilitate seamless communication between `localhost` ports (`1234–1237` and `5180`).
   - For production deployments, replace `HttpClientHandler.DangerousAcceptAnyServerCertificateValidator` with strict TLS certificate validation and configure valid X.509 certificates.

3. **Model Context Protocol (MCP) Access Control**
   - Both the `stdio` MCP server (`HomeManagementMcpServer`) and the HTTP MCP endpoint (`POST /mcp`) allow connected AI clients to query device telemetry and trigger configured device actions.
   - Only connect trusted AI clients and review tool invocations when operating on physical hardware.

---

## Reporting a Vulnerability

If you discover a security vulnerability in this repository:

1. **Do not open a public GitHub issue** containing live exploit details.
2. Report the issue privately via GitHub Security Advisories on the repository (`https://github.com/oleksandrmyhydiuk/dataart-itcamp/security/advisories`) or contact the repository maintainer directly.
3. Include steps to reproduce, affected components (`HomeManagementDeviceApi`, `HomeManagementDashboard`, or `HomeManagementMcpServer`), and potential impact.

We aim to acknowledge security reports within **48 hours** and provide a remediation or mitigation update within **7 days**.
