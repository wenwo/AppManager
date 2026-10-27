# build.ps1 - AppManager single-file build (ASCII only for PS 5.1 GBK safety)
# Compiles all .cs under src\ into a single zero-dependency AppManager.exe
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = "Stop"
$base = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $base "src"
$out  = Join-Path $base "AppManager.exe"

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
$mpv = Join-Path $base "libmpv-2.dll"
if (-not (Test-Path $mpv)) {
  $fetch = Join-Path $base "tools\fetch-libmpv.ps1"
  if ((Test-Path $fetch) -and ($env:APPMANAGER_SKIP_LIBMPV_FETCH -ne "1")) {
    Write-Host "libmpv-2.dll missing; downloading it via tools\fetch-libmpv.ps1 ..."
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $fetch
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

& $csc /nologo /target:winexe /utf8output /out:$out @icoArg @manifestArg @refArgs $files
$code = $LASTEXITCODE
if ($code -ne 0) {
  Write-Error "Build failed (exit $code)"
  exit $code
}

# selftest (bounded wait so a stuck run can never hang the build)
Write-Host "Running --selftest ..."
$p = Start-Process -FilePath $out -ArgumentList "--selftest" -PassThru
if (-not $p.WaitForExit(120000)) {
  try { $p.Kill() } catch { }
  Write-Error "Selftest timeout (120s)."
  exit 1
}
$code = $p.ExitCode
$log = Join-Path $base "selftest.log"
if (Test-Path $log) { Get-Content $log -Raw | Write-Host }

# Exit code convention: 0 = all cases passed, 1 = failure
if ($code -eq 0) {
  Write-Host "Selftest PASSED (exit code 0)."
  exit 0
} else {
  Write-Error ("Selftest FAILED (exit code " + $code + ").")
  exit 1
}
