param(
    [string]$UnityExe = 'D:\6000.6.5f1\Editor\Unity.exe',
    [string]$ProjectPath = 'D:\afk\AFKIsland',
    [string]$LogFile = 'D:\afk\AFKIsland\Builds\unity-android-build.log',
    [string]$ExecuteMethod = 'AfkIsland.EditorTools.AndroidBuild.BuildApkBatch'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $UnityExe)) { throw "Unity not found: $UnityExe" }
if (-not (Test-Path $ProjectPath)) { throw "Project not found: $ProjectPath" }

New-Item -ItemType Directory -Force -Path (Split-Path $LogFile -Parent) | Out-Null

# Default method writes Builds/Android/AFKIsland.apk (see AndroidBuild.cs).
$unityArgs = @(
    '-batchmode',
    '-nographics',
    '-projectPath', $ProjectPath,
    '-buildTarget', 'Android',
    '-executeMethod', $ExecuteMethod,
    '-logFile', $LogFile
)

Write-Host "Starting Unity: $UnityExe"
Write-Host ($unityArgs -join ' ')
$p = Start-Process -FilePath $UnityExe -ArgumentList $unityArgs -Wait -PassThru
Write-Host "Unity exit: $($p.ExitCode)"
Write-Host "Log: $LogFile"
if ($p.ExitCode -ne 0) { exit $p.ExitCode }

Get-ChildItem (Join-Path $ProjectPath 'Builds') -Recurse -Filter *.apk -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 5 FullName, Length, LastWriteTime
exit 0
