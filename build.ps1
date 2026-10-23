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
  "Microsoft.VisualBasic.dll"
)

$ico = Join-Path $base "AppManager.ico"
$icoArg = @()
if (-not (Test-Path $ico)) {
  $mic = Join-Path $base "tools\make-icon.ps1"
  if (Test-Path $mic) { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $mic | Out-Null }
}
if (Test-Path $ico) { $icoArg = @("/win32icon:$ico") }

$refArgs = $refs | ForEach-Object { "/r:$_" }

Write-Host "Compiler : $csc"
Write-Host "Sources  : $($files.Count) file(s)"
Write-Host "Output   : $out"

& $csc /nologo /target:winexe /utf8output /out:$out @icoArg @refArgs $files
$code = $LASTEXITCODE
if ($code -ne 0) {
  Write-Error "Build failed (exit $code)"
  exit $code
}

# selftest
Write-Host "Running --selftest ..."
$p = Start-Process -FilePath $out -ArgumentList "--selftest" -Wait -PassThru -NoNewWindow
$log = Join-Path $base "selftest.log"
if (Test-Path $log) { Get-Content $log -Raw | Write-Host }

# Exit code convention: 0 = all cases passed, 1 = failure
if ($p.ExitCode -eq 0) {
  Write-Host "Selftest PASSED (exit code 0)."
  exit 0
} else {
  Write-Error ("Selftest FAILED (exit code " + $p.ExitCode + ").")
  exit 1
}
