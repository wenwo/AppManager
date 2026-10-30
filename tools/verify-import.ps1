$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $env:TEMP ('AppManagerImportTests_' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    $sources = Get-ChildItem (Join-Path $project 'src') -Recurse -Filter *.cs | ForEach-Object FullName
    $references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
        'System.Xml.dll', 'Microsoft.VisualBasic.dll', 'Microsoft.CSharp.dll') | ForEach-Object { '/r:' + $_ }
    $references += @('WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll', 'System.Runtime.dll', 'System.Runtime.WindowsRuntime.dll') |
        ForEach-Object { '/r:' + (Join-Path (Split-Path -Parent $compiler) $_) }
    $references += @('Windows.Foundation.winmd', 'Windows.Data.winmd', 'Windows.Storage.winmd') |
        ForEach-Object { '/r:' + (Join-Path $env:WINDIR ('System32\WinMetadata\' + $_)) }
    & $compiler /nologo /target:exe /platform:x64 /utf8output /main:AppManager.FileImportTests "/out:$testExe" @references @sources (Join-Path $PSScriptRoot 'FileImportTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Import test compilation failed.' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Import tests failed.' }
}
finally { if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe } }
