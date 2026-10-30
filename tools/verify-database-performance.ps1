param([string]$BaselineRevision = '468a3ab', [int[]]$Counts = @(10000, 100000, 300000), [string]$OutputDirectory = '', [string]$LiveDatabasePath = '')
$ErrorActionPreference = 'Stop'
$perfProject = Split-Path -Parent $PSScriptRoot
$perfRoot = Join-Path ([IO.Path]::GetTempPath()) ('AppManagerPerformance_' + [Guid]::NewGuid().ToString('N'))
if ($OutputDirectory) { $perfRoot = [IO.Path]::GetFullPath($OutputDirectory) }
if (Test-Path -LiteralPath $perfRoot) { throw 'Output directory must not already exist.' }
[IO.Directory]::CreateDirectory($perfRoot) | Out-Null
$perfBaseline = Join-Path $perfRoot 'baseline'
[IO.Directory]::CreateDirectory($perfBaseline) | Out-Null
$perfArchive = Join-Path $perfRoot 'baseline.zip'
$perfCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$perfProbe = Join-Path $PSScriptRoot 'DatabasePerformanceProbe.cs'
$perfLog = Join-Path $perfRoot 'results.log'
& git -C $perfProject archive --format=zip "--output=$perfArchive" $BaselineRevision
if ($LASTEXITCODE -ne 0) { throw 'Cannot export baseline revision.' }
Expand-Archive -LiteralPath $perfArchive -DestinationPath $perfBaseline
$perfRefs = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','Microsoft.VisualBasic.dll','Microsoft.CSharp.dll') | ForEach-Object { '/r:' + $_ }
$perfOld = Join-Path $perfRoot 'XML.exe'
$perfNew = Join-Path $perfRoot 'SQLite.exe'
foreach ($perfBuild in @(@($perfBaseline, $perfOld, ''), @($perfProject, $perfNew, '/define:DATABASE'))) {
    $perfFiles = Get-ChildItem (Join-Path $perfBuild[0] 'src') -Filter *.cs -Recurse | ForEach-Object FullName
    $perfExtra = @()
    if ($perfBuild[2]) { $perfExtra += $perfBuild[2] }
    & $perfCompiler /nologo /target:exe /platform:x64 /utf8output /main:AppManager.DatabasePerformanceProbe "/out:$($perfBuild[1])" @perfExtra @perfRefs $perfFiles $perfProbe
    if ($LASTEXITCODE -ne 0) { throw 'Probe compilation failed.' }
}
Write-Output "RESULTS=$perfRoot"
function Run-PerformanceProbe([string]$exe, [string[]]$probeArguments) {
    $probeOut = Join-Path $perfRoot ('stdout-' + [Guid]::NewGuid().ToString('N') + '.log')
    $probeErr = $probeOut + '.err'
    $quotedArguments = @($probeArguments | ForEach-Object { '"' + $_ + '"' })
    $probeProcess = Start-Process -FilePath $exe -ArgumentList $quotedArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $probeOut -RedirectStandardError $probeErr
    $probeHandle = $probeProcess.Handle
    try {
        $probeDeadline = [DateTime]::UtcNow.AddMinutes(8)
        while (-not $probeProcess.WaitForExit(1000)) {
            if ([DateTime]::UtcNow -gt $probeDeadline) { throw 'Probe timed out after 8 minutes.' }
        }
        $probeProcess.Refresh()
        Get-Content -LiteralPath $probeOut -Encoding UTF8 | Tee-Object -FilePath $perfLog -Append
        if ($probeProcess.ExitCode -ne 0) { Get-Content -LiteralPath $probeErr; throw 'Probe failed.' }
    } finally {
        if (-not $probeProcess.HasExited) { $probeProcess.Kill(); $probeProcess.WaitForExit(5000) | Out-Null }
        $probeProcess.Dispose()
    }
}
foreach ($perfCount in $Counts) {
    $perfFixture = Join-Path $perfRoot ('items-' + $perfCount)
    Run-PerformanceProbe $perfNew @('generate', $perfFixture, "$perfCount")
    Run-PerformanceProbe $perfNew @('migrate', $perfFixture, "$perfCount")
    # Alternate execution order by size to limit systematic order bias; OS file cache is not flushed.
    $perfOrder = @($perfOld, $perfNew)
    if ($perfCount -eq 100000) { $perfOrder = @($perfNew, $perfOld) }
    foreach ($perfExe in $perfOrder) { Run-PerformanceProbe $perfExe @('run', $perfFixture, "$perfCount") }
}
# Dense relation metadata is a separate workload; do not hide compatibility-layer costs.
$perfDense = Join-Path $perfRoot 'dense-10000'
Write-Output 'DENSE_RELATIONS=10000'
Run-PerformanceProbe $perfNew @('generate', $perfDense, '10000', 'dense')
Run-PerformanceProbe $perfNew @('migrate', $perfDense, '10000')
foreach ($perfExe in @($perfOld, $perfNew)) { Run-PerformanceProbe $perfExe @('run', $perfDense, '10000') }
if ($LiveDatabasePath) {
    $perfUser = Join-Path $perfRoot 'current-user'
    $perfSnapshot = @(Run-PerformanceProbe $perfNew @('snapshot', $perfUser, '0', [IO.Path]::GetFullPath($LiveDatabasePath)))
    $perfSnapshot | Write-Output
    $perfUserCount = ($perfSnapshot | Where-Object { $_ -like 'SNAPSHOT count=*' } | Select-Object -Last 1).Split('=')[1]
    foreach ($perfExe in @($perfOld, $perfNew)) { Run-PerformanceProbe $perfExe @('user', $perfUser, "$perfUserCount") }
}
Write-Output "DONE results=$perfLog"
