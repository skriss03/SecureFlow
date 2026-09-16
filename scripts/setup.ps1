# SecureFlow day-zero setup for Windows. Run from an elevated PowerShell:
#   Set-ExecutionPolicy -Scope Process Bypass; .\scripts\setup.ps1
# Idempotent: skips anything already installed.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Has($cmd) { return $null -ne (Get-Command $cmd -ErrorAction SilentlyContinue) }

Write-Host "== Toolchain ==" -ForegroundColor Cyan
if (-not (Has 'winget')) { throw "winget is required (App Installer from the Microsoft Store)." }

if (-not (Has 'dotnet') -or -not ((dotnet --list-sdks) -match '^10\.')) {
    Write-Host "Installing .NET 10 SDK..."
    winget install --id Microsoft.DotNet.SDK.10 --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
} else { Write-Host ".NET SDK present: $(dotnet --version)" }

if (-not (Has 'node')) {
    Write-Host "Installing Node.js LTS..."
    winget install --id OpenJS.NodeJS.LTS --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
} else { Write-Host "Node present: $(node --version)" }

if (-not (Has 'git')) {
    Write-Host "Installing Git..."
    winget install --id Git.Git --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
} else { Write-Host "Git present: $(git --version)" }

# Refresh PATH for this session after installs.
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

Write-Host "`n== API keys ==" -ForegroundColor Cyan
foreach ($k in 'ANTHROPIC_API_KEY', 'OPENAI_API_KEY') {
    $v = [Environment]::GetEnvironmentVariable($k, 'User')
    if ([string]::IsNullOrWhiteSpace($v)) {
        Write-Host "$k is not set. Set it with:  [Environment]::SetEnvironmentVariable('$k', '<key>', 'User')" -ForegroundColor Yellow
    } else { Write-Host "$k is set ($($v.Length) chars)" }
}

Write-Host "`n== Build ==" -ForegroundColor Cyan
Push-Location $root
try {
    dotnet build SecureFlow.slnx --nologo -v q
    dotnet test tests/SecureFlow.Core.Tests/SecureFlow.Core.Tests.csproj --nologo -v q
    Push-Location web
    try {
        npm install --no-audit --no-fund --loglevel=error
        npm run build
    } finally { Pop-Location }
} finally { Pop-Location }

Write-Host "`nDone. Start with:  .\scripts\run.ps1   then open http://localhost:5180" -ForegroundColor Green
