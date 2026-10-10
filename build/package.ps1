<#
.SYNOPSIS
  Builds a runnable JARVIS for Windows: the dashboard UI, the runtime (jarvis-core.exe),
  the desktop interface (JARVIS.exe), a portable zip and an installer.

.EXAMPLE
  ./build/package.ps1                 # everything, version from Directory.Build.props
  ./build/package.ps1 -Version 0.3.5  # explicit version
  ./build/package.ps1 -SkipInstaller  # no Inno Setup needed

  Output: artifacts/JARVIS-Setup-x64.exe, artifacts/JARVIS-Portable-x64.zip, artifacts/app/
#>
param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [switch]$SkipUi,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts"
$app = Join-Path $out "app"
$tfm = "net10.0-windows10.0.19041.0"

if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
    $Version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1).'#text'
    if (-not $Version) { $Version = "0.3.5" }
}
Write-Host "Packaging JARVIS $Version" -ForegroundColor Cyan

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $app | Out-Null

# 1. Dashboard UI (React) -> src/Jarvis.Runtime/wwwroot
if (-not $SkipUi -and (Test-Path (Join-Path $root "ui/package.json"))) {
    Push-Location (Join-Path $root "ui")
    try {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "UI build failed" }
    } finally { Pop-Location }
}

# 2. Runtime and desktop shell, self-contained (no .NET install needed on the target PC).
$common = @("-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-p:Version=$Version", "-p:DebugType=none", "-o", $app)
dotnet publish (Join-Path $root "src/Jarvis.Runtime/Jarvis.Runtime.csproj") -f $tfm @common
if ($LASTEXITCODE -ne 0) { throw "Runtime publish failed" }

$desktop = Join-Path $root "src/Jarvis.Desktop/Jarvis.Desktop.csproj"
if (Test-Path $desktop) {
    dotnet publish $desktop @common
    if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed" }
}

Copy-Item (Join-Path $root "assets/jarvis.ico") $app
Copy-Item (Join-Path $root "README.md") $app -ErrorAction SilentlyContinue
Set-Content -Path (Join-Path $app "version.txt") -Value $Version

# 3. Portable zip
$zip = Join-Path $out "JARVIS-Portable-x64.zip"
Compress-Archive -Path (Join-Path $app "*") -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Portable: $zip"

# 4. Installer (Inno Setup 6)
if (-not $SkipInstaller) {
    $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
        foreach ($c in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
            if (Test-Path $c) { $iscc = $c; break }
        }
    }
    if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install it (winget install JRSoftware.InnoSetup) or pass -SkipInstaller." }
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$app" "/O$out" (Join-Path $root "installer/jarvis.iss")
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
    Write-Host "Installer: $(Join-Path $out 'JARVIS-Setup-x64.exe')"
}

Write-Host "Done." -ForegroundColor Green
