# Install the `secureflow` command for the current user (Windows). After this, in ANY project folder:
#
#     secureflow scan
#
# Usage:
#   .\scripts\install-cli.ps1                                   # from this repo's artifacts/ (local test)
#   .\scripts\install-cli.ps1 -BaseUrl https://artifactory.example.com/artifactory/generic-local/secureflow
#   irm https://artifactory.example.com/.../install-cli.ps1 | iex   # once the script itself is published
#
# Everything is a parameter or an environment variable; nothing is installed system-wide and no admin
# rights are needed. The binary is self-contained: no .NET, Java, Node or Python required to run it.
param(
    [string]$BaseUrl    = $env:SECUREFLOW_CLI_BASE_URL,
    [string]$Version    = $(if ($env:SECUREFLOW_CLI_VERSION) { $env:SECUREFLOW_CLI_VERSION } else { '0.1.0' }),
    [string]$InstallDir = $(if ($env:SECUREFLOW_INSTALL_DIR) { $env:SECUREFLOW_INSTALL_DIR } else { Join-Path $HOME '.secureflow\bin' }),
    [string]$Server     = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Default for the local test: the artifacts/cli folder produced by scripts/publish-clients.ps1.
if (-not $BaseUrl -and $PSScriptRoot) {
    $local = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\cli'
    if (Test-Path $local) { $BaseUrl = ([Uri]$local).AbsoluteUri }
}
if (-not $BaseUrl) { throw "Set -BaseUrl or SECUREFLOW_CLI_BASE_URL to the folder that holds <version>/secureflow-<rid>.exe." }

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$file = "secureflow-win-$arch.exe"
$url = "$($BaseUrl.TrimEnd('/'))/$Version/$file"
$target = Join-Path $InstallDir 'secureflow.exe'

Write-Host "Installing secureflow $Version" -ForegroundColor Cyan
Write-Host "  from $url"
Write-Host "  to   $target"
New-Item -ItemType Directory -Force $InstallDir | Out-Null
$tmp = "$target.download"
if ($url.StartsWith('file:')) { Copy-Item ([Uri]$url).LocalPath $tmp -Force }
else { Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing }
Move-Item $tmp $target -Force

# Put the install folder first on the user PATH so this version wins over any older `secureflow`.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @($userPath -split ';' | Where-Object { $_ -and ($_.TrimEnd('\') -ne $InstallDir.TrimEnd('\')) })
[Environment]::SetEnvironmentVariable('Path', (@($InstallDir) + $parts) -join ';', 'User')
$env:Path = "$InstallDir;" + (($env:Path -split ';' | Where-Object { $_.TrimEnd('\') -ne $InstallDir.TrimEnd('\') }) -join ';')

if ($Server) {
    $cfgDir = Join-Path $HOME '.secureflow'
    $cfg = Join-Path $cfgDir 'config.yml'
    New-Item -ItemType Directory -Force $cfgDir | Out-Null
    $lines = if (Test-Path $cfg) { @(Get-Content $cfg | Where-Object { $_ -notmatch '^\s*server\s*:' }) } else { @() }
    Set-Content -Path $cfg -Value (@("server: $Server") + $lines) -Encoding utf8
    Write-Host "  server set to $Server in $cfg"
}

# Warn about other copies that could confuse people (e.g. an old `dotnet tool` install).
$others = @(Get-Command secureflow -All -ErrorAction SilentlyContinue | Where-Object { $_.Source -and ($_.Source -ne $target) })
foreach ($o in $others) {
    Write-Warning "Another secureflow is also on PATH: $($o.Source). This install comes first; remove the other one to avoid confusion."
}

Write-Host ""
& $target --version
Write-Host ""
Write-Host "Done. Open a NEW terminal, go to any project folder and run:" -ForegroundColor Green
Write-Host "    secureflow scan"
