<#
.SYNOPSIS
  Starts the packaged JARVIS on this Windows machine, drives it through its API like a user
  would, captures screenshots, and shuts it down. Used by CI; safe to run locally.
#>
param(
    [string]$AppDir = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts/app"),
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts/smoke")
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$data = Join-Path $env:TEMP ("jarvis-smoke-" + [guid]::NewGuid().ToString("n"))
$env:JARVIS_DATA_DIR = $data
$env:JARVIS_DEV = "1"   # never register a CI build to start with Windows
$failures = @()

function Check($name, [scriptblock]$test) {
    try {
        $r = & $test
        Write-Host "PASS  $name  $r" -ForegroundColor Green
    } catch {
        Write-Host "FAIL  $name  $($_.Exception.Message)" -ForegroundColor Red
        $script:failures += $name
    }
}

$core = Start-Process -FilePath (Join-Path $AppDir "jarvis-core.exe") -PassThru
try {
    $info = $null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        $infoPath = Join-Path $data "runtime.json"
        if (Test-Path $infoPath) {
            $info = Get-Content $infoPath -Raw | ConvertFrom-Json
            try { Invoke-RestMethod "http://127.0.0.1:$($info.port)/api/health" | Out-Null; break } catch { }
        }
    }
    if (-not $info) { throw "Runtime did not start (no runtime.json)" }
    $base = "http://127.0.0.1:$($info.port)"
    $headers = @{ "X-Jarvis-Token" = $info.token }
    function Api($method, $path, $body = $null) {
        $req = @{ Method = $method; Uri = "$base$path"; Headers = $headers; ContentType = "application/json; charset=utf-8" }
        if ($body) { $req.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 8)) }
        Invoke-RestMethod @req
    }
    function Say($text) { $r = Api POST "/api/chat" @{ text = $text }; "$($r.route): $($r.reply)" }

    Check "status" { $s = Api GET "/api/status"; "v$($s.version) platform=$($s.platform) presence=$($s.presence.snapshot.state)" }
    Check "unauthenticated request is refused" {
        try { Invoke-RestMethod "$base/api/status" | Out-Null; throw "expected 401" } catch { if ($_.Exception.Response.StatusCode.value__ -ne 401) { throw } }
        "401"
    }
    Check "dashboard is served" { $html = Invoke-WebRequest "$base/" -UseBasicParsing; if ($html.Content -notmatch "<html") { throw "no html" }; "ok" }
    Check "time (English)" { Say "what time is it" }
    Check "time (Arabic)" { Say "الساعة كام؟" }
    Check "system status" { Say "how's the system" }
    Check "task" { Say "add task Prepare the CityCrep briefing" }
    Check "reminder (Arabic)" { Say "فكرني بعد 10 دقايق اكلم احمد" }
    Check "memory" { Say "remember that the CityCrep meeting is on Sunday"; Say "what do you know about CityCrep" }
    Check "open notepad" {
        $r = Say "open notepad"
        Start-Sleep 2
        if (-not (Get-Process notepad -ErrorAction SilentlyContinue)) { throw "notepad not running: $r" }
        $r
    }
    Check "screenshot" { Say "take a screenshot" }
    Check "close notepad" { Say "close notepad" }
    Check "read-only command" { Say "run git --version" }
    Check "no-AI question explains how to enable AI" { $r = Say "why is my build failing?"; if ($r -notmatch "Ollama") { throw $r }; "ok" }
    Check "activity log" { $a = Api GET "/api/activity?limit=5"; "$($a.Count) entries" }

    Start-Sleep 3
    # Desktop capture for the CI artifact (shows the orb if the desktop shell is running).
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "desktop.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()

    Get-ChildItem (Join-Path ([Environment]::GetFolderPath("MyPictures")) "JARVIS") -ErrorAction SilentlyContinue | Copy-Item -Destination $OutDir
    Check "desktop shell running" { $p = Get-Process JARVIS -ErrorAction SilentlyContinue; if (-not $p -and (Test-Path (Join-Path $AppDir "JARVIS.exe"))) { throw "JARVIS.exe not running" }; "ok" }

    Check "shutdown" { Api POST "/api/runtime/shutdown" | Out-Null; if (-not $core.WaitForExit(15000)) { throw "runtime did not exit" }; "exit $($core.ExitCode)" }
} finally {
    if (-not $core.HasExited) { $core.Kill() }
    Get-Process JARVIS -ErrorAction SilentlyContinue | Stop-Process -Force
    Copy-Item (Join-Path $data "logs\*") $OutDir -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) { throw "Smoke test failures: $($failures -join ', ')" }
Write-Host "Smoke test passed." -ForegroundColor Green
