# Start SecureFlow.
#   .\scripts\run.ps1            -> API + built UI on http://localhost:5180 (demo mode)
#   .\scripts\run.ps1 -Dev       -> API on :5180 and Vite dev server on http://localhost:5173 with hot reload
#   .\scripts\run.ps1 -Offline   -> never call an AI provider; serve only replay-cached responses (proves the demo cannot fail)
param([switch]$Dev, [switch]$Offline)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
if ($Offline) { $env:SECUREFLOW_AI_OFFLINE = '1' } else { Remove-Item Env:SECUREFLOW_AI_OFFLINE -ErrorAction SilentlyContinue }

if (-not (Test-Path "$root/src/SecureFlow.Api/wwwroot/index.html") -and -not $Dev) {
    Write-Host "UI not built yet; building..." -ForegroundColor Yellow
    Push-Location "$root/web"; try { npm run build } finally { Pop-Location }
}

if ($Dev) {
    Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root/web'; npm run dev"
}
Push-Location $root
try { dotnet run --project src/SecureFlow.Api } finally { Pop-Location }
