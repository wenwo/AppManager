$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$verificationDir = Join-Path ([IO.Path]::GetTempPath()) ('AppManagerVerify_' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($verificationDir) | Out-Null
$app = Join-Path $verificationDir 'AppManager.verify.exe'
$regression = Join-Path $verificationDir 'AppManager.regression.exe'
$process = $null
try {
    $sources = Get-ChildItem (Join-Path $project 'src') -Recurse -Filter *.cs | ForEach-Object FullName
    $references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
        'System.Xml.dll', 'Microsoft.VisualBasic.dll', 'Microsoft.CSharp.dll') | ForEach-Object { '/r:' + $_ }
    $references += @('WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll') |
        ForEach-Object { '/r:' + (Join-Path (Split-Path -Parent $compiler) $_) }
    $manifest = '/win32manifest:' + (Join-Path $project 'app.manifest')
    $references += '/r:' + (Join-Path (Split-Path -Parent $compiler) 'System.Runtime.dll')
    $references += '/r:' + (Join-Path (Split-Path -Parent $compiler) 'System.Runtime.WindowsRuntime.dll')
    $references += @('Windows.Foundation.winmd', 'Windows.Data.winmd', 'Windows.Storage.winmd') |
        ForEach-Object { '/r:' + (Join-Path $env:WINDIR ('System32\WinMetadata\' + $_)) }
    & $compiler /nologo /target:winexe /utf8output "/out:$app" $manifest @references @sources
    if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
    $process = Start-Process -FilePath $app -ArgumentList '--selftest' -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while (-not $process.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Selftest timed out.' }
    }
    Get-Content (Join-Path $env:LOCALAPPDATA 'AppManager\selftest.log') -Encoding UTF8 | Select-Object -Last 1
    if ($process.ExitCode -ne 0) { throw 'Selftest failed.' }
    & $compiler /nologo /target:exe /main:AppManager.RegressionTests /utf8output "/out:$regression" $manifest @references @sources (Join-Path $PSScriptRoot 'RegressionTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Regression compilation failed.' }
    & $regression
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    if ($process) { $process.Dispose() }
    [IO.File]::Delete($app)
    [IO.File]::Delete($regression)
    [IO.Directory]::Delete($verificationDir)
}
