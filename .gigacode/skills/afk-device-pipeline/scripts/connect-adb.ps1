param(
    [int[]]$Ports = @(5555, 5556, 5565, 5575, 5585, 5595, 5610, 6610, 6620)
)

$ErrorActionPreference = 'Stop'
$adb = Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
if (-not (Test-Path $adb)) { throw "adb not found: $adb" }

Write-Host "adb: $adb"
& $adb start-server | Out-Null

foreach ($p in $Ports) {
    Write-Host "connect 127.0.0.1:$p"
    & $adb connect "127.0.0.1:$p" | Out-Host
}

Write-Host '--- devices ---'
& $adb devices -l
$ready = & $adb devices | Select-String -Pattern "`tdevice$"
if (-not $ready) {
    Write-Host 'No device yet. Start Desktop shortcut "BlueStacks (игра)", wait until Android boots, re-run this script.'
    exit 1
}
exit 0
