param(
    [Parameter(Mandatory = $true)][string]$ApkPath,
    [string]$Package = 'com.afkisland.game'
)

$ErrorActionPreference = 'Stop'
$adb = Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
if (-not (Test-Path $ApkPath)) { throw "APK not found: $ApkPath" }

& $PSScriptRoot\connect-adb.ps1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "install -r $ApkPath"
& $adb install -r $ApkPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "force-stop $Package"
& $adb shell am force-stop $Package
Write-Host "start $Package"
& $adb shell monkey -p $Package -c android.intent.category.LAUNCHER 1
exit 0
