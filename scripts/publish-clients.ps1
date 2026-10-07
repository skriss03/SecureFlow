# Build every SecureFlow client artifact into one folder: the local stand-in for Artifactory.
#
#   artifacts/
#     cli/<version>/secureflow-<rid>[.exe]     native single-file CLI per OS (no .NET needed to run it)
#     nuget/SecureFlow.Cli.<version>.nupkg     dotnet tool
#     npm/secureflow-cli-<version>.tgz         npm wrapper            (-Wrappers)
#     python/secureflow_cli-<version>-*.whl    pip wrapper            (-Wrappers)
#     maven: installed into ~/.m2               Maven + Gradle plugins (-Wrappers; needs mvn/gradle, see install-build-tools.ps1)
#
# The wrappers download the native CLI from <CliBaseUrl>/<version>/secureflow-<rid>. For the local test
# that is this folder; after publishing to Artifactory it is the Artifactory path. Point clients at it
# with SECUREFLOW_CLI_BASE_URL (printed at the end).
#
#   .\scripts\publish-clients.ps1                         # CLI binaries + dotnet tool
#   .\scripts\publish-clients.ps1 -Wrappers               # ... plus npm, pip, Maven and Gradle wrappers
#   .\scripts\publish-clients.ps1 -Runtimes win-x64       # only the binary you need locally
param(
    [string]$OutDir   = $(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts'),
    [string[]]$Runtimes = @('win-x64', 'linux-x64', 'osx-arm64'),
    [string]$Version  = '',
    [string]$Configuration = 'Release',
    [switch]$Wrappers
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cliProj = Join-Path $root 'src/SecureFlow.Cli/SecureFlow.Cli.csproj'
if (-not $Version) { $Version = ([xml](Get-Content $cliProj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $Version) { throw "No -Version given and none found in $cliProj" }

$cliDir = Join-Path $OutDir "cli/$Version"
New-Item -ItemType Directory -Force $cliDir, (Join-Path $OutDir 'nuget') | Out-Null

foreach ($rid in $Runtimes) {
    Write-Host "Publishing secureflow CLI $Version for $rid ..." -ForegroundColor Cyan
    $tmp = Join-Path $OutDir "tmp/$rid"
    dotnet publish $cliProj -c $Configuration -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:DebugType=none -p:Version=$Version -o $tmp --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }
    $ext = if ($rid -like 'win-*') { '.exe' } else { '' }
    Copy-Item (Join-Path $tmp "secureflow$ext") (Join-Path $cliDir "secureflow-$rid$ext") -Force
}
Remove-Item (Join-Path $OutDir 'tmp') -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Packing dotnet tool ..." -ForegroundColor Cyan
dotnet pack $cliProj -c $Configuration -p:Version=$Version -o (Join-Path $OutDir 'nuget') --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed" }

$cliBaseUrl = ([Uri](Join-Path $OutDir 'cli')).AbsoluteUri

# npm, pip, mvn and gradle print progress on stderr, which Windows PowerShell turns into terminating
# errors under ErrorActionPreference=Stop. Run them with Continue and judge by the exit code instead.
function Invoke-Native([string]$What, [scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command 2>&1 | ForEach-Object { "$_" } | Out-Host } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" }
}

if ($Wrappers) {
    Write-Host "Packing npm wrapper ..." -ForegroundColor Cyan
    $npmOut = Join-Path $OutDir 'npm'
    New-Item -ItemType Directory -Force $npmOut | Out-Null
    Push-Location (Join-Path $root 'clients/npm')
    try { Invoke-Native 'npm pack' { npm pack --silent --pack-destination $npmOut } } finally { Pop-Location }

    Write-Host "Building Python wrapper ..." -ForegroundColor Cyan
    $pyOut = Join-Path $OutDir 'python'
    New-Item -ItemType Directory -Force $pyOut | Out-Null
    Invoke-Native 'pip wheel' { python -m pip wheel (Join-Path $root 'clients/python') --no-deps -w $pyOut -q }

    if (Get-Command mvn -ErrorAction SilentlyContinue) {
        Write-Host "Installing Maven plugin into ~/.m2 ..." -ForegroundColor Cyan
        Invoke-Native 'Maven plugin build' { mvn -q -B -f (Join-Path $root 'clients/maven-plugin/pom.xml') install }
    } else { Write-Warning "mvn not on PATH; skipped the Maven plugin (run scripts/install-build-tools.ps1)." }

    Write-Host "Publishing Gradle plugin to ~/.m2 ..." -ForegroundColor Cyan
    Push-Location (Join-Path $root 'clients/gradle-plugin')
    $gradlew = if ($env:OS -eq 'Windows_NT') { '.\gradlew.bat' } else { './gradlew' }
    try { Invoke-Native 'Gradle plugin build' { & $gradlew -q --no-daemon publishToMavenLocal } } finally { Pop-Location }
}

Write-Host ""
Write-Host "Artifacts in $OutDir" -ForegroundColor Green
Get-ChildItem $OutDir -Recurse -File | ForEach-Object { Write-Host ("  " + $_.FullName.Substring($OutDir.Length + 1)) }
Write-Host ""
Write-Host "Point the wrappers at these binaries (until they live in Artifactory):" -ForegroundColor Green
Write-Host "  `$env:SECUREFLOW_CLI_BASE_URL = '$cliBaseUrl'"
Write-Host "dotnet tool:  dotnet tool install -g SecureFlow.Cli --add-source $(Join-Path $OutDir 'nuget') --version $Version"
