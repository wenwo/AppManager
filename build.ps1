# build.ps1 - AppManager single-file build (ASCII only for PS 5.1 GBK safety)
# Compiles all .cs under src\ into a single zero-dependency AppManager.exe.
# The default output is the runtime/data directory (%LOCALAPPDATA%\AppManager).
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-OutputPath <candidate.exe>]
param([string]$OutputPath = "", [switch]$ScaleDatabaseTests)

$ErrorActionPreference = "Stop"
$base = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $base "src"
$runtime = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "AppManager"
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
$out  = Join-Path $runtime "AppManager.exe"
if ($OutputPath) { $out = [System.IO.Path]::GetFullPath($OutputPath) }

$running = @(Get-Process -Name AppManager -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -and [string]::Equals($_.Path, $out, [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) {
  Write-Error "AppManager is running from $out. Exit it from the tray menu before building."
  exit 5
}

$csc = @(
  "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
  "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $csc) {
  Write-Error "csc.exe not found. .NET Framework 4.x required."
  exit 2
}

$files = Get-ChildItem -Path $src -Filter *.cs -Recurse | ForEach-Object { $_.FullName }
if ($files.Count -eq 0) {
  Write-Error "No source files under $src"
  exit 3
}

$refs = @(
  "System.dll",
  "System.Core.dll",
  "System.Drawing.dll",
  "System.Windows.Forms.dll",
  "System.Xml.dll",
  "Microsoft.VisualBasic.dll",
  "Microsoft.CSharp.dll"
)

# WIC image decoding uses the framework's WPF imaging wrapper; these are inbox
# .NET Framework assemblies and do not add a third-party runtime dependency.
$frameworkDir = Split-Path -Parent $csc
$wpfRefs = @('WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll') |
  ForEach-Object { Join-Path $frameworkDir $_ } | Where-Object { Test-Path $_ }
if ($wpfRefs.Count -ne 3) {
  Write-Error "WPF imaging assemblies not found. WIC image decoding cannot be built."
  exit 4
}
$refs += $wpfRefs
$refs += @('System.Runtime.dll', 'System.Runtime.WindowsRuntime.dll') | ForEach-Object { Join-Path $frameworkDir $_ }
$refs += @('Windows.Foundation.winmd', 'Windows.Data.winmd', 'Windows.Storage.winmd') |
  ForEach-Object { Join-Path $env:WINDIR ('System32\WinMetadata\' + $_) }

$ico = Join-Path $base "AppManager.ico"
$icoArg = @()
if (-not (Test-Path $ico)) {
  $mic = Join-Path $base "tools\make-icon.ps1"
  if (Test-Path $mic) { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $mic | Out-Null }
}
if (Test-Path $ico) { $icoArg = @("/win32icon:$ico") }

$manifest = Join-Path $base "app.manifest"
$manifestArg = @()
if (Test-Path $manifest) { $manifestArg = @("/win32manifest:$manifest") }

$refArgs = $refs | ForEach-Object { "/r:$_" }

# Internal video player needs the mpv client library next to the exe (too large for the repo).
# Set APPMANAGER_SKIP_LIBMPV_FETCH=1 to skip the automatic download (offline builds).
$mpv = Join-Path $runtime "libmpv-2.dll"
if (-not (Test-Path $mpv)) {
  $fetch = Join-Path $base "tools\fetch-libmpv.ps1"
  if ((Test-Path $fetch) -and ($env:APPMANAGER_SKIP_LIBMPV_FETCH -ne "1")) {
    Write-Host "libmpv-2.dll missing; downloading it via tools\fetch-libmpv.ps1 ..."
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $fetch -Destination $mpv
    if ($LASTEXITCODE -ne 0) {
      Write-Warning "libmpv-2.dll download failed. The video player will show a placeholder until the library is placed next to AppManager.exe."
    }
  } else {
    Write-Warning "libmpv-2.dll not found. The in-app video player will show a placeholder until the library is placed next to AppManager.exe."
  }
}

Write-Host "Compiler : $csc"
Write-Host "Sources  : $($files.Count) file(s)"
Write-Host "Output   : $out"

& $csc /nologo /target:winexe /platform:x64 /utf8output /out:$out @icoArg @manifestArg @refArgs $files
$code = $LASTEXITCODE
if ($code -ne 0) {
  Write-Error "Build failed (exit $code)"
  exit $code
}

# selftest (bounded wait so a stuck run can never hang the build)
Write-Host "Running --selftest ..."
$p = Start-Process -FilePath $out -ArgumentList "--selftest" -WindowStyle Hidden -PassThru
if (-not $p.WaitForExit(120000)) {
  try { $p.Kill() } catch { }
  Write-Error "Selftest timeout (120s)."
  exit 1
}
$code = $p.ExitCode
$log = Join-Path $runtime "selftest.log"
if (Test-Path $log) { Get-Content $log -Raw -Encoding UTF8 | Write-Host }

# Exit code convention: 0 = all cases passed, 1 = failure
if ($code -eq 0) {
  Write-Host "Selftest PASSED (exit code 0)."
  $dbTests = Join-Path $env:TEMP ("AppManagerDatabaseTests_" + [Guid]::NewGuid().ToString("N") + ".exe")
  try {
    & $csc /nologo /target:exe /platform:x64 /utf8output /main:AppManager.DatabaseTests /out:$dbTests @manifestArg @refArgs $files (Join-Path $base "tools\DatabaseTests.cs")
    if ($LASTEXITCODE -ne 0) { throw "Database test compilation failed." }
    $dbArgs = @()
    if ($ScaleDatabaseTests) { $dbArgs = @("--scale") }
    & $dbTests @dbArgs
    if ($LASTEXITCODE -ne 0) { throw "Database tests failed." }
  } finally {
    if (Test-Path -LiteralPath $dbTests) { Remove-Item -LiteralPath $dbTests }
  }
  exit 0
} else {
  Write-Error ("Selftest FAILED (exit code " + $code + ").")
  exit 1
}
