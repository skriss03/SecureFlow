# Download Maven and Gradle for building the SecureFlow Java clients (maven-plugin, gradle-plugin) and the
# Spring sample apps on a machine that has a JDK but no build tools. Nothing is installed system-wide:
# the tools land in $ToolsDir and this session's PATH is updated. Developers who already have Maven/Gradle,
# or use the committed mvnw / gradlew wrappers, never need this.
#
#   .\scripts\install-build-tools.ps1
#   .\scripts\install-build-tools.ps1 -MavenBaseUrl https://artifactory.example.com/artifactory/maven-remote
#
# Every location and version is a parameter (or SECUREFLOW_* env var) so a corporate mirror can be used.
param(
    [string]$ToolsDir      = $(if ($env:SECUREFLOW_TOOLS_DIR) { $env:SECUREFLOW_TOOLS_DIR } else { Join-Path $HOME '.secureflow\tools' }),
    [string]$MavenVersion  = $(if ($env:SECUREFLOW_MAVEN_VERSION) { $env:SECUREFLOW_MAVEN_VERSION } else { '3.9.9' }),
    [string]$GradleVersion = $(if ($env:SECUREFLOW_GRADLE_VERSION) { $env:SECUREFLOW_GRADLE_VERSION } else { '8.10.2' }),
    [string]$MavenBaseUrl  = $(if ($env:SECUREFLOW_MAVEN_BASE_URL) { $env:SECUREFLOW_MAVEN_BASE_URL } else { 'https://repo.maven.apache.org/maven2' }),
    [string]$GradleBaseUrl = $(if ($env:SECUREFLOW_GRADLE_BASE_URL) { $env:SECUREFLOW_GRADLE_BASE_URL } else { 'https://services.gradle.org/distributions' }),
    [switch]$SkipMaven,
    [switch]$SkipGradle
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Force $ToolsDir | Out-Null

function Get-Tool([string]$Name, [string]$Url, [string]$HomeDir) {
    if (Test-Path (Join-Path $HomeDir 'bin')) { Write-Host "$Name already present at $HomeDir"; return }
    $zip = Join-Path $ToolsDir "$Name.zip"
    Write-Host "Downloading $Name from $Url ..."
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $ToolsDir -Force
    Remove-Item $zip
    Write-Host "$Name installed at $HomeDir"
}

$paths = @()
if (-not $SkipMaven) {
    $mavenHome = Join-Path $ToolsDir "apache-maven-$MavenVersion"
    Get-Tool 'maven' "$MavenBaseUrl/org/apache/maven/apache-maven/$MavenVersion/apache-maven-$MavenVersion-bin.zip" $mavenHome
    $paths += Join-Path $mavenHome 'bin'
}
if (-not $SkipGradle) {
    $gradleHome = Join-Path $ToolsDir "gradle-$GradleVersion"
    Get-Tool 'gradle' "$GradleBaseUrl/gradle-$GradleVersion-bin.zip" $gradleHome
    $paths += Join-Path $gradleHome 'bin'
}

$env:Path = ($paths -join ';') + ';' + $env:Path
Write-Host ""
Write-Host "Added to PATH for this session:" -ForegroundColor Green
$paths | ForEach-Object { Write-Host "  $_" }
Write-Host "To keep them for new terminals, add those folders to your user PATH."
