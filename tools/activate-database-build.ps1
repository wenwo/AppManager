# Run after exiting AppManager from its tray menu. Never terminates the running app.
$ErrorActionPreference = "Stop"
$activateRoot = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "AppManager"
$activateCandidate = Join-Path $activateRoot "AppManager.Database.exe"
$activateTarget = Join-Path $activateRoot "AppManager.exe"
if (-not (Test-Path -LiteralPath $activateCandidate)) { throw "Build AppManager.Database.exe first." }
$activateRunning = @(Get-Process -Name AppManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $activateTarget })
if ($activateRunning.Count) { throw "Exit AppManager using its tray menu, then run this script again." }
$activateBackups = Join-Path $activateRoot "Backups\Binaries"
New-Item -ItemType Directory -Path $activateBackups -Force | Out-Null
$activatePrevious = Join-Path $activateBackups ("AppManager-before-sqlite-" + (Get-Date -Format "yyyyMMdd-HHmmss-fff") + ".exe")
if (Test-Path -LiteralPath $activateTarget) { Copy-Item -LiteralPath $activateTarget -Destination $activatePrevious }
$activateStaged = Join-Path $activateRoot ("AppManager.install-" + [Guid]::NewGuid().ToString("N") + ".exe")
try {
    Copy-Item -LiteralPath $activateCandidate -Destination $activateStaged
    if (Test-Path -LiteralPath $activateTarget) { [System.IO.File]::Replace($activateStaged, $activateTarget, [NullString]::Value) }
    else { [System.IO.File]::Move($activateStaged, $activateTarget) }
    Write-Host "Installed AppManager.exe. Start it normally to migrate XML to SQLite."
    Write-Host "Previous executable: $activatePrevious"
} finally {
    if (Test-Path -LiteralPath $activateStaged) { Remove-Item -LiteralPath $activateStaged }
}
