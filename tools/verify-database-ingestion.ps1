param([string]$LiveDatabasePath = '')
$ErrorActionPreference = 'Stop'
$ingestProject = Split-Path -Parent $PSScriptRoot
$ingestCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$ingestRoot = Join-Path ([IO.Path]::GetTempPath()) ('AppManagerDatabaseVerification_' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($ingestRoot) | Out-Null
$ingestFiles = Get-ChildItem (Join-Path $ingestProject 'src') -Recurse -Filter *.cs | ForEach-Object FullName
$ingestRefs = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','Microsoft.VisualBasic.dll','Microsoft.CSharp.dll') | ForEach-Object { '/r:' + $_ }
$ingestRefs += @('WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll') |
    ForEach-Object { '/r:' + (Join-Path (Split-Path -Parent $ingestCompiler) $_) }
$ingestLog = Join-Path $ingestRoot 'results.log'
$ingestRefs += '/r:' + (Join-Path (Split-Path -Parent $ingestCompiler) 'System.Runtime.dll')
$ingestRefs += '/r:' + (Join-Path (Split-Path -Parent $ingestCompiler) 'System.Runtime.WindowsRuntime.dll')
$ingestRefs += @('Windows.Foundation.winmd', 'Windows.Data.winmd', 'Windows.Storage.winmd') |
    ForEach-Object { '/r:' + (Join-Path $env:WINDIR ('System32\WinMetadata\' + $_)) }
Write-Output "RESULTS=$ingestRoot"
$ingestFailed = $false
foreach ($ingestTest in @('DatabaseTests', 'DatabaseIngestionTests')) {
    $ingestExe = Join-Path $ingestRoot ($ingestTest + '.exe')
    & $ingestCompiler /nologo /target:exe /platform:x64 /utf8output "/main:AppManager.$ingestTest" "/out:$ingestExe" @ingestRefs $ingestFiles (Join-Path $PSScriptRoot ($ingestTest + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw 'Verification compilation failed.' }
    $ingestOut = Join-Path $ingestRoot ($ingestTest + '.log')
    $ingestErr = $ingestOut + '.err'
    $ingestStart = @{ FilePath = $ingestExe; WindowStyle = 'Hidden'; PassThru = $true; RedirectStandardOutput = $ingestOut; RedirectStandardError = $ingestErr }
    if ($ingestTest -eq 'DatabaseIngestionTests' -and $LiveDatabasePath) { $ingestStart.ArgumentList = '"' + [IO.Path]::GetFullPath($LiveDatabasePath) + '"' }
    $ingestProcess = Start-Process @ingestStart
    $ingestHandle = $ingestProcess.Handle
    try {
        $ingestDeadline = [DateTime]::UtcNow.AddSeconds(120)
        while (-not $ingestProcess.WaitForExit(1000)) {
            if ([DateTime]::UtcNow -gt $ingestDeadline) { throw 'Verification timed out after 120 seconds.' }
        }
        $ingestProcess.Refresh()
        Get-Content -LiteralPath $ingestOut -Encoding UTF8 | Tee-Object -FilePath $ingestLog -Append
        if ($ingestProcess.ExitCode -ne 0) { $ingestFailed = $true; Get-Content -LiteralPath $ingestErr -Encoding UTF8 }
        Write-Output ($ingestTest + '_EXIT=' + $ingestProcess.ExitCode)
    } finally {
        if (-not $ingestProcess.HasExited) { $ingestProcess.Kill(); $ingestProcess.WaitForExit(5000) | Out-Null }
        $ingestProcess.Dispose()
    }
}
Write-Output "DONE results=$ingestLog"
if ($ingestFailed) { exit 1 }
