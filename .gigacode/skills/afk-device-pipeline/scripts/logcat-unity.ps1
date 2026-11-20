param(
    [string]$Package = 'com.afkisland.game',
    [int]$Seconds = 20,
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
$adb = Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
& $PSScriptRoot\connect-adb.ps1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $adb logcat -c | Out-Null
$pidLine = & $adb shell pidof $Package
$pid = ($pidLine | Out-String).Trim()
Write-Host "pidof ${Package}: '$pid'"

$args = @('logcat', '-d', '-v', 'time')
if ($pid) { $args = @('logcat', '-d', '-v', 'time', "--pid=$pid") }

Start-Sleep -Seconds ([Math]::Max(1, $Seconds))
$raw = & $adb @args | Out-String
$filtered = $raw -split "`r?`n" | Where-Object {
    $_ -match 'Unity|Exception|Error|AFK|InvalidOperation|ClassNotFound'
}

if ($OutFile) {
    $dir = Split-Path $OutFile -Parent
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $filtered | Set-Content -Path $OutFile -Encoding UTF8
    Write-Host "wrote $($filtered.Count) lines -> $OutFile"
} else {
    $filtered | Select-Object -Last 200
}
exit 0
