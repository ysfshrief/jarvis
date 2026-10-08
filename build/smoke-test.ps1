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
    Check "reminder is listed" {
        $r = @(Api GET "/api/reminders")
        if ($r.Count -lt 1) { $n = Api GET "/api/notifications"; throw "no pending reminders; notifications: $($n | ConvertTo-Json -Depth 3 -Compress)" }
        "$($r.Count) pending, due $($r[0].dueAt)"
    }
    Check "connectivity" { Start-Sleep 2; $s = Api GET "/api/status"; "online=$($s.online)" }
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
    Check "no-AI question explains how to enable AI" { $r = Say "summarize my week for me"; if ($r -notmatch "Ollama") { throw $r }; "ok" }
    Check "activity log" { $a = Api GET "/api/activity?limit=5"; "$($a.Count) entries" }
    Check "daily briefing" { $r = Say "what's happening today"; if ($r -notmatch "deterministic") { throw $r }; $r }
    Check "system metrics" {
        Api GET "/api/system/metrics" | Out-Null; Start-Sleep 1.2
        $m = Api GET "/api/system/metrics"
        if ($null -eq $m.current.cpuPercent -or $null -eq $m.current.memoryPercent) { throw "cpu/memory not reported" }
        "cpu=$($m.current.cpuPercent)% mem=$($m.current.memoryPercent)% gpu=$($m.current.gpuPercent) source=$($m.source)"
    }

    Start-Sleep 3
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    function Capture($name) {
        $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
        $g.Dispose()
        $bmp.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        # A small JPEG copy goes into the log too, so the result can be checked without downloading artifacts.
        $w = [Math]::Min(960, $bmp.Width); $h = [int]($bmp.Height * $w / $bmp.Width)
        $small = New-Object System.Drawing.Bitmap $bmp, $w, $h
        $ms = New-Object System.IO.MemoryStream
        $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq "image/jpeg" }
        $ep = New-Object System.Drawing.Imaging.EncoderParameters 1
        $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 60L
        $small.Save($ms, $codec, $ep)
        Write-Host ("SCREENSHOT " + $name + " " + [Convert]::ToBase64String($ms.ToArray()))
        $small.Dispose(); $bmp.Dispose()
    }
    Capture "desktop"

    # Open the dashboard window (WebView2) through the runtime, as a second instance would.
    if (Test-Path (Join-Path $AppDir "JARVIS.exe")) {
        Check "desktop shell connected to the event stream" {
            $s = Api GET "/api/status"
            if ($s.eventClients -lt 1) { throw "no event-stream clients (desktop shell not connected)" }
            "$($s.eventClients) client(s)"
        }
        Check "dashboard window opens and the web UI authenticates" {
            $before = (Api GET "/api/status").eventClients
            Api POST "/api/ui/show" | Out-Null
            $deadline = (Get-Date).AddSeconds(25)
            do { Start-Sleep 1; $now = (Api GET "/api/status").eventClients } while ($now -le $before -and (Get-Date) -lt $deadline)
            $titles = (Get-Process JARVIS -ErrorAction SilentlyContinue | ForEach-Object { $_.MainWindowTitle }) -join ", "
            if ($now -le $before) { throw "the dashboard (WebView2) did not connect; windows: $titles" }
            "event clients $before -> $now; windows: $titles"
        }
    }
    Capture "dashboard"

    if (Test-Path (Join-Path $AppDir "JARVIS.exe")) {
        Check "command console opens with Ctrl+Alt+J" {
            $before = (Api GET "/api/status").eventClients
            [System.Windows.Forms.SendKeys]::SendWait("^%j")
            $deadline = (Get-Date).AddSeconds(20)
            do { Start-Sleep 1; $now = (Api GET "/api/status").eventClients } while ($now -le $before -and (Get-Date) -lt $deadline)
            if ($now -le $before) { throw "the console page did not connect (event clients stayed at $before)" }
            "event clients $before -> $now"
        }
        Start-Sleep 2
        Capture "console"
    }

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
