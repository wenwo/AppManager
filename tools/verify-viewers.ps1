$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$output = Join-Path ([IO.Path]::GetTempPath()) ('AppManagerViewers_' + [Guid]::NewGuid().ToString('N') + '.exe')
$process = $null
$stdout = $output + '.out'
$stderr = $output + '.err'
try {
    $sources = Get-ChildItem (Join-Path $project 'src') -Recurse -Filter *.cs | ForEach-Object FullName
    $refs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
        'System.Xml.dll', 'Microsoft.VisualBasic.dll', 'Microsoft.CSharp.dll')
    $refs += @('WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll',
        'System.Runtime.dll', 'System.Runtime.WindowsRuntime.dll') | ForEach-Object { Join-Path $framework $_ }
    $refs += @('Windows.Foundation.winmd', 'Windows.Data.winmd', 'Windows.Storage.winmd') |
        ForEach-Object { Join-Path $env:WINDIR ('System32\WinMetadata\' + $_) }
    $refArgs = $refs | ForEach-Object { '/r:' + $_ }
    & $compiler /nologo /target:exe /platform:x64 /utf8output /main:AppManager.ViewerTests "/out:$output" @refArgs @sources (Join-Path $PSScriptRoot 'ViewerTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Viewer test compilation failed.' }
    $process = Start-Process -FilePath $output -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $null = $process.Handle
    if (-not $process.WaitForExit(180000)) { throw 'Viewer tests timed out.' }
    Get-Content $stdout -Encoding UTF8
    Get-Content $stderr -Encoding UTF8
    if ($process.ExitCode -ne 0) { throw 'Viewer tests failed.' }
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    if ($process) { $process.Dispose() }
    foreach ($file in @($output, $stdout, $stderr)) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
}
