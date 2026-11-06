<#
.SYNOPSIS
  Records the raw stdout of `fvm flutter run --machine` for a scenario (fixture for DaemonProtocol / run session tests).

.PARAMETER Device    Flutter device id (e.g. emulator-5554, windows, chrome).
.PARAMETER Scenario  start-ok | reload-ok | restart-ok | reload-compile-error | runtime-exception | stop | build-fail | web-reload-unsupported
.PARAMETER Out       Output file. Receives stdout lines exactly as emitted (UTF-8, no BOM, LF).
.PARAMETER ProjectDir Flutter project to run (default: tests/sample_app).
.PARAMETER TimeoutSec Hard cap per scenario (default 300).

  Compatible with Windows PowerShell 5.1. lib/main.dart is always restored afterwards.
#>
param(
    [Parameter(Mandatory = $true)][string]$Device,
    [Parameter(Mandatory = $true)][ValidateSet('start-ok', 'reload-ok', 'restart-ok', 'reload-compile-error', 'runtime-exception', 'stop', 'build-fail', 'web-reload-unsupported')][string]$Scenario,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$StderrOut = '',
    [string]$ProjectDir = '',
    [int]$TimeoutSec = 300
)

$ErrorActionPreference = 'Stop'
if ($ProjectDir -eq '') {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $ProjectDir = Join-Path $scriptDir '..\tests\sample_app'
}
$ProjectDir = (Resolve-Path $ProjectDir).Path
$mainDart = Join-Path $ProjectDir 'lib\main.dart'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$outDir = Split-Path -Parent ([IO.Path]::GetFullPath($Out))
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }
$Out = [IO.Path]::GetFullPath($Out)
if ($StderrOut -eq '') { $StderrOut = [IO.Path]::ChangeExtension($Out, '.stderr.txt') }
$StderrOut = [IO.Path]::GetFullPath($StderrOut)

$originalBytes = [IO.File]::ReadAllBytes($mainDart)
$script:lines = New-Object System.Collections.ArrayList
$script:errs = [System.Collections.ArrayList]::Synchronized((New-Object System.Collections.ArrayList))
$script:sync = New-Object object
$script:nextId = 1
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
$proc = $null

function Get-LineCount { [System.Threading.Monitor]::Enter($script:sync); try { return $script:lines.Count } finally { [System.Threading.Monitor]::Exit($script:sync) } }

function Wait-ForLine([string]$pattern, [int]$fromIndex, [int]$maxSeconds) {
    # Returns the matching line (or $null on timeout / process exit). Never exceeds the global deadline.
    $limit = [DateTime]::UtcNow.AddSeconds($maxSeconds)
    if ($limit -gt $deadline) { $limit = $deadline }
    while ([DateTime]::UtcNow -lt $limit) {
        [System.Threading.Monitor]::Enter($script:sync)
        try {
            for ($i = $fromIndex; $i -lt $script:lines.Count; $i++) {
                if ($script:lines[$i] -match $pattern) { return $script:lines[$i] }
            }
        } finally { [System.Threading.Monitor]::Exit($script:sync) }
        if ($proc.HasExited) { Start-Sleep -Milliseconds 300; break }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Send-Request([string]$method, [hashtable]$params) {
    $id = $script:nextId
    $script:nextId++
    $json = ConvertTo-Json -Compress -InputObject @(@{ id = $id; method = $method; params = $params })
    $proc.StandardInput.Write($json + "`n")
    $proc.StandardInput.Flush()
    return $id
}

try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'cmd.exe'
    $argLine = "/c fvm flutter run --machine -d $Device"
    if ($Scenario -eq 'build-fail') {
        # Android/iOS/macOS: an unknown flavor makes the native build fail. Other devices (web, windows) only warn
        # about --flavor, so there we break the Dart source instead (restored in the finally block).
        if ($Device -like 'emulator-*') { $argLine += ' --flavor doesnotexist' }
        else {
            $src = [IO.File]::ReadAllText($mainDart)
            [IO.File]::WriteAllText($mainDart, $src + "`nint x = ;`n", $utf8NoBom)
        }
    }
    $psi.Arguments = $argLine
    $psi.WorkingDirectory = $ProjectDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = $utf8NoBom
    $psi.StandardErrorEncoding = $utf8NoBom

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $null = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -SourceIdentifier 'rec-out' -Action {
        if ($null -ne $EventArgs.Data) {
            [System.Threading.Monitor]::Enter($Event.MessageData.sync)
            try { $null = $Event.MessageData.lines.Add($EventArgs.Data) } finally { [System.Threading.Monitor]::Exit($Event.MessageData.sync) }
        }
    } -MessageData @{ sync = $script:sync; lines = $script:lines }
    $null = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -SourceIdentifier 'rec-err' -Action {
        if ($null -ne $EventArgs.Data) { $null = $Event.MessageData.errs.Add($EventArgs.Data) }
    } -MessageData @{ errs = $script:errs }
    $null = $proc.Start()
    $proc.BeginOutputReadLine()
    $proc.BeginErrorReadLine()
    Write-Host "[$Scenario] started pid $($proc.Id) on device $Device"

    if ($Scenario -eq 'build-fail') {
        # No app.started expected: wait for the process to exit on its own.
        while (-not $proc.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
        Start-Sleep -Milliseconds 500
    }
    else {
        $startedLine = Wait-ForLine '"event":"app\.started"' 0 240
        if ($null -eq $startedLine) { throw "[$Scenario] app.started was not seen (timeout or process exit)" }
        $appStartLine = $null
        foreach ($l in $script:lines.ToArray()) { if ($l -match '"event":"app\.start"') { $appStartLine = $l; break } }
        if ($appStartLine -notmatch '"appId":"([^"]+)"') { throw "[$Scenario] could not find appId in app.start" }
        $appId = $Matches[1]
        Write-Host "[$Scenario] app.started, appId=$appId"
        Start-Sleep -Seconds 2

        $respPattern = { param($id) '"id":' + $id + '[,}]' }
        switch ($Scenario) {
            'start-ok' { Start-Sleep -Seconds 3 }
            'stop' {
                $mark = Get-LineCount
                $id = Send-Request 'app.stop' @{ appId = $appId }
                $null = Wait-ForLine (& $respPattern $id) $mark 30
                $null = Wait-ForLine '"event":"app\.stop"' $mark 30
            }
            { $_ -in 'reload-ok', 'web-reload-unsupported' } {
                $mark = Get-LineCount
                $id = Send-Request 'app.restart' @{ appId = $appId; fullRestart = $false; pause = $false; reason = 'manual' }
                if ($null -eq (Wait-ForLine (& $respPattern $id) $mark 90)) { throw "[$Scenario] no response to reload" }
                Start-Sleep -Seconds 2
            }
            'restart-ok' {
                $mark = Get-LineCount
                $id = Send-Request 'app.restart' @{ appId = $appId; fullRestart = $true; pause = $false; reason = 'manual' }
                if ($null -eq (Wait-ForLine (& $respPattern $id) $mark 90)) { throw "[$Scenario] no response to restart" }
                Start-Sleep -Seconds 2
            }
            'reload-compile-error' {
                $src = [IO.File]::ReadAllText($mainDart)
                [IO.File]::WriteAllText($mainDart, $src + "`nint x = ;`n", $utf8NoBom)
                $mark = Get-LineCount
                $id = Send-Request 'app.restart' @{ appId = $appId; fullRestart = $false; pause = $false; reason = 'manual' }
                if ($null -eq (Wait-ForLine (& $respPattern $id) $mark 90)) { throw "[$Scenario] no response to reload" }
                Start-Sleep -Seconds 2
            }
            'runtime-exception' {
                $src = [IO.File]::ReadAllText($mainDart)
                $patched = [regex]::Replace($src, '(class _MyHomePageState[\s\S]*?Widget build\(BuildContext context\) \{)', "`$1`n    throw StateError('boom');", 1)
                if ($patched -eq $src) { throw "[$Scenario] could not patch build()" }
                [IO.File]::WriteAllText($mainDart, $patched, $utf8NoBom)
                $mark = Get-LineCount
                $id = Send-Request 'app.restart' @{ appId = $appId; fullRestart = $false; pause = $false; reason = 'manual' }
                $null = Wait-ForLine (& $respPattern $id) $mark 90
                $header = Wait-ForLine 'Exception caught by' $mark 20
                if ($null -ne $header) {
                    # Footer: a later line made of a long run of box-drawing characters (U+2550).
                    $hIdx = $script:lines.IndexOf($header, $mark)
                    $box = [string][char]0x2550
                    $null = Wait-ForLine ('(' + $box + '){20,}') ($hIdx + 1) 20
                }
                Start-Sleep -Seconds 1
            }
        }

        if (-not $proc.HasExited) {
            if ($Scenario -ne 'stop' -and $Scenario -ne 'start-ok') {
                $mark = Get-LineCount
                $null = Send-Request 'app.stop' @{ appId = $appId }
                $null = Wait-ForLine '"event":"app\.stop"' $mark 30
            }
        }
    }
}
finally {
    # Restore lib/main.dart first, always.
    [IO.File]::WriteAllBytes($mainDart, $originalBytes)
    if ($null -ne $proc -and -not $proc.HasExited) {
        $null = $proc.WaitForExit(5000)
        if (-not $proc.HasExited) {
            Write-Host "[$Scenario] killing process tree $($proc.Id)"
            & taskkill.exe /T /F /PID $proc.Id | Out-Null
            $null = $proc.WaitForExit(10000)
        }
    }
    Start-Sleep -Milliseconds 500
    Unregister-Event -SourceIdentifier 'rec-out' -ErrorAction SilentlyContinue
    Unregister-Event -SourceIdentifier 'rec-err' -ErrorAction SilentlyContinue
    # stderr goes to a sidecar file (cross-stream ordering with stdout is not recorded).
    $errFile = $StderrOut
    [IO.File]::WriteAllText($errFile, (($script:errs.ToArray()) -join "`n"), $utf8NoBom)
    Write-Host "[$Scenario] stderr ($($script:errs.Count) lines) saved to $errFile"
    [System.Threading.Monitor]::Enter($script:sync)
    try {
        $sb =New-Object System.Text.StringBuilder
        foreach ($l in $script:lines) { $null = $sb.Append($l).Append("`n") }
        [IO.File]::WriteAllText($Out, $sb.ToString(), $utf8NoBom)
        Write-Host "[$Scenario] wrote $($script:lines.Count) lines to $Out"
    } finally { [System.Threading.Monitor]::Exit($script:sync) }
}
