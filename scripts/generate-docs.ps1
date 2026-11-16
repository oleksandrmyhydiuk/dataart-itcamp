param(
    [string]$Configuration = "Debug",
    [string]$OutputDir = "_site"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# Collect C# source metadata & XML documentation comments across all projects
$projects = @(
    @{ Name = "HomeManagementDeviceApi"; Description = "Smart Home IoT Device Emulator Minimal API (/info, /config, /command, /restart, /shutdown)" },
    @{ Name = "HomeManagementDashboard"; Description = "Blazor Interactive Server Monitoring Dashboard, Workflow Automation Engine & HTTP MCP Hub" },
    @{ Name = "HomeManagementMcpServer"; Description = "Standalone Model Context Protocol (MCP) JSON-RPC 2.0 Server over stdio for AI Clients" },
    @{ Name = "HomeManagement.Tests"; Description = "xUnit Automated Unit & Integration Test Suite for Telemetry, Dashboard, Workflows & MCP" }
)

$typeEntries = @()
$csFiles = Get-ChildItem -Path . -Recurse -Filter "*.cs" | Where-Object {
    $_.FullName -notmatch "[\\/](obj|bin|\.git)[\\/]"
}

foreach ($file in $csFiles) {
    $relPath = Resolve-Path -Relative $file.FullName
    $content = Get-Content $file.FullName -Raw
    $nsMatch = [regex]::Match($content, "namespace\s+([A-Za-z0-9_.]+)")
    $ns = if ($nsMatch.Success) { $nsMatch.Groups[1].Value } else { "Global" }

    $typeMatches = [regex]::Matches($content, "public\s+(?:static\s+|sealed\s+|abstract\s+)?(class|record|enum)\s+([A-Za-z0-9_]+)")
    foreach ($m in $typeMatches) {
        $kind = $m.Groups[1].Value
        $typeName = $m.Groups[2].Value

        $methodMatches = [regex]::Matches($content, "public\s+(?:static\s+|async\s+|override\s+)*([A-Za-z0-9_<>,\?\.\[\]\(\)\s]+)\s+([A-Za-z0-9_]+)\s*\(([^)]*)\)")
        $methods = @()
        foreach ($mm in $methodMatches) {
            $mName = $mm.Groups[2].Value
            if ($mName -ne $typeName -and $mName -notin @("if", "for", "while", "switch", "lock")) {
                $retType = ($mm.Groups[1].Value -replace "\s+", " ").Trim()
                $params = ($mm.Groups[3].Value -replace "\s+", " ").Trim()
                $methods += "$retType $mName($params)"
            }
        }

        $typeEntries += [PSCustomObject]@{
            Namespace = $ns
            Kind      = $kind
            Name      = $typeName
            File      = $relPath
            Methods   = ($methods | Select-Object -Unique)
        }
    }
}

$cardsHtml = ""
foreach ($entry in ($typeEntries | Sort-Object Namespace, Name)) {
    $methodsHtml = ""
    if ($entry.Methods.Count -gt 0) {
        $items = ($entry.Methods | ForEach-Object { "<li><code>$([System.Net.WebUtility]::HtmlEncode($_))</code></li>" }) -join ""
        $methodsHtml = "<div class='methods-title'>Public Methods &amp; Signatures</div><ul class='methods-list'>$items</ul>"
    }
    $cardsHtml += @"
    <div class="doc-card" data-search="$($entry.Name.ToLower()) $($entry.Namespace.ToLower())">
        <div class="doc-card-header">
            <span class="badge badge-$($entry.Kind)">$($entry.Kind.ToUpper())</span>
            <span class="ns-tag">$($entry.Namespace)</span>
        </div>
        <h3>$($entry.Name)</h3>
        <div class="file-path">Source: <code>$($entry.File)</code></div>
        $methodsHtml
    </div>
"@
}

$projectsHtml = ""
foreach ($p in $projects) {
    $projectsHtml += @"
    <div class="proj-card">
        <h3>$($p.Name)</h3>
        <p>$($p.Description)</p>
    </div>
"@
}

$generatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'")

$indexHtml = @"
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>HomeManagement — Code &amp; API Documentation</title>
    <style>
        :root {
            --bg: #0b1120;
            --card: #111827;
            --border: rgba(148, 163, 184, 0.18);
            --text: #f8fafc;
            --muted: #94a3b8;
            --accent: #38bdf8;
            --emerald: #10b981;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            background: var(--bg);
            color: var(--text);
            font-family: 'Segoe UI', Inter, system-ui, sans-serif;
            line-height: 1.6;
            padding: 32px 24px 64px;
        }
        .container { max-width: 1280px; margin: 0 auto; }
        header {
            border-bottom: 1px solid var(--border);
            padding-bottom: 24px;
            margin-bottom: 28px;
            display: flex;
            justify-content: space-between;
            align-items: flex-end;
            flex-wrap: wrap;
            gap: 16px;
        }
        h1 { font-size: 1.9rem; font-weight: 800; }
        .subtitle { color: var(--muted); font-size: 0.95rem; margin-top: 4px; }
        .meta { color: var(--accent); font-size: 0.82rem; }
        h2 { font-size: 1.3rem; margin: 28px 0 14px; color: #e2e8f0; }
        .proj-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
            gap: 16px;
        }
        .proj-card {
            background: var(--card);
            border: 1px solid var(--border);
            border-radius: 12px;
            padding: 18px;
            border-top: 3px solid var(--accent);
        }
        .proj-card h3 { font-size: 1.05rem; margin-bottom: 6px; }
        .proj-card p { font-size: 0.86rem; color: var(--muted); }
        .search-bar {
            width: 100%;
            max-width: 440px;
            background: var(--card);
            border: 1px solid var(--border);
            color: var(--text);
            padding: 10px 14px;
            border-radius: 10px;
            font-size: 0.92rem;
            margin-bottom: 18px;
        }
        .doc-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(380px, 1fr));
            gap: 16px;
        }
        .doc-card {
            background: var(--card);
            border: 1px solid var(--border);
            border-radius: 12px;
            padding: 18px;
            display: flex;
            flex-direction: column;
            gap: 8px;
        }
        .doc-card-header { display: flex; justify-content: space-between; align-items: center; }
        .badge {
            font-size: 0.68rem;
            font-weight: 700;
            padding: 2px 8px;
            border-radius: 999px;
            background: rgba(56, 189, 248, 0.16);
            color: var(--accent);
        }
        .badge-enum { background: rgba(16, 185, 129, 0.16); color: var(--emerald); }
        .badge-record { background: rgba(168, 85, 247, 0.18); color: #c084fc; }
        .ns-tag { font-size: 0.78rem; color: var(--muted); font-family: Consolas, monospace; }
        .file-path { font-size: 0.78rem; color: var(--muted); }
        .methods-title { font-size: 0.75rem; text-transform: uppercase; color: var(--accent); font-weight: 700; margin-top: 6px; }
        .methods-list { list-style: none; display: flex; flex-direction: column; gap: 4px; font-size: 0.8rem; }
        .methods-list code { color: #cbd5e1; font-family: Consolas, monospace; word-break: break-all; }
    </style>
</head>
<body>
    <div class="container">
        <header>
            <div>
                <h1>HomeManagement — Code &amp; API Reference</h1>
                <p class="subtitle">Automated Code Documentation • .NET 11.0 • Smart Device API, Blazor Dashboard, Workflow Engine &amp; MCP Server</p>
            </div>
            <div class="meta">Generated: $generatedAt</div>
        </header>

        <h2>Solution Architecture Modules</h2>
        <div class="proj-grid">
            $projectsHtml
        </div>

        <h2>Types, Services &amp; Models Reference ($($typeEntries.Count) documented types)</h2>
        <input type="text" id="filterInput" class="search-bar" placeholder="Filter classes, records, enums or namespaces..." onkeyup="filterCards()" />

        <div class="doc-grid" id="cardsContainer">
            $cardsHtml
        </div>
    </div>

    <script>
        function filterCards() {
            const q = document.getElementById('filterInput').value.toLowerCase();
            document.querySelectorAll('.doc-card').forEach(card => {
                const text = card.getAttribute('data-search') || '';
                card.style.display = text.includes(q) ? '' : 'none';
            });
        }
    </script>
</body>
</html>
"@

Set-Content -Path (Join-Path $OutputDir "index.html") -Value $indexHtml -Encoding UTF8
New-Item -ItemType File -Force -Path (Join-Path $OutputDir ".nojekyll") | Out-Null
Write-Host "Documentation portal generated at $OutputDir/index.html ($($typeEntries.Count) types documented)."
