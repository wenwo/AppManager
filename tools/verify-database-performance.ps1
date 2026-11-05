param([string]$BaselineRevision = '468a3ab', [string]$SQLiteBaselineRevision = '', [int[]]$Counts = @(10000, 100000, 300000), [string]$OutputDirectory = '', [string]$LiveDatabasePath = '')
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
$perfSqliteBaseline = $null
$perfSqliteOld = $null
if ($SQLiteBaselineRevision) {
    if ($SQLiteBaselineRevision -eq $BaselineRevision) {
        $perfSqliteBaseline = $perfBaseline
    } else {
        $perfSqliteBaseline = Join-Path $perfRoot 'sqlite-baseline'
        [IO.Directory]::CreateDirectory($perfSqliteBaseline) | Out-Null
        $perfSqliteArchive = Join-Path $perfRoot 'sqlite-baseline.zip'
        & git -C $perfProject archive --format=zip "--output=$perfSqliteArchive" $SQLiteBaselineRevision
        if ($LASTEXITCODE -ne 0) { throw 'Cannot export SQLite baseline revision.' }
        Expand-Archive -LiteralPath $perfSqliteArchive -DestinationPath $perfSqliteBaseline
    }
    $perfSqliteOld = Join-Path $perfRoot 'SQLite-baseline.exe'
    Write-Output "SQLITE_BASELINE_REVISION=$SQLiteBaselineRevision"
}
$perfRefs = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','Microsoft.VisualBasic.dll','Microsoft.CSharp.dll', 'System.IO.Compression.dll') | ForEach-Object { '/r:' + $_ }
$perfRefs += @('WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll') |
    ForEach-Object { '/r:' + (Join-Path (Split-Path -Parent $perfCompiler) $_) }
$perfOld = Join-Path $perfRoot 'XML.exe'
$perfRefs += '/r:' + (Join-Path (Split-Path -Parent $perfCompiler) 'System.Runtime.dll')
$perfRefs += '/r:' + (Join-Path (Split-Path -Parent $perfCompiler) 'System.Runtime.WindowsRuntime.dll')
$perfRefs += @('Windows.Foundation.winmd', 'Windows.Data.winmd', 'Windows.Graphics.winmd', 'Windows.Storage.winmd') |
    ForEach-Object { '/r:' + (Join-Path $env:WINDIR ('System32\WinMetadata\' + $_)) }
$perfNew = Join-Path $perfRoot 'SQLite.exe'
function Compile-PerformanceProbe([string]$source, [string]$exe, [string]$define) {
    $perfFiles = Get-ChildItem (Join-Path $source 'src') -Filter *.cs -Recurse | ForEach-Object FullName
    $perfExtra = @()
    if ($define) { $perfExtra += $define }
    & $perfCompiler /nologo /target:exe /platform:x64 /utf8output /main:AppManager.DatabasePerformanceProbe "/out:$exe" @perfExtra @perfRefs $perfFiles $perfProbe
    if ($LASTEXITCODE -ne 0) { throw 'Probe compilation failed.' }
}
Compile-PerformanceProbe $perfBaseline $perfOld ''
Compile-PerformanceProbe $perfProject $perfNew '/define:DATABASE'
if ($perfSqliteOld) { Compile-PerformanceProbe $perfSqliteBaseline $perfSqliteOld '/define:DATABASE' }
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
    $perfSqliteBaselineFixture = $null
    if ($perfSqliteOld) {
        $perfSqliteBaselineFixture = Join-Path $perfRoot ('items-' + $perfCount + '-sqlite-baseline')
        [IO.Directory]::CreateDirectory($perfSqliteBaselineFixture) | Out-Null
        Copy-Item -LiteralPath (Join-Path $perfFixture 'config.xml') -Destination (Join-Path $perfSqliteBaselineFixture 'config.xml')
        Run-PerformanceProbe $perfSqliteOld @('migrate', $perfSqliteBaselineFixture, "$perfCount")
    }
    # Alternate execution order by size to limit systematic order bias; OS file cache is not flushed.
    $perfRuns = @(
        [pscustomobject]@{ Exe = $perfOld; Label = 'XML-baseline'; Fixture = $perfFixture },
        [pscustomobject]@{ Exe = $perfNew; Label = 'SQLite-current'; Fixture = $perfFixture }
    )
    $perfStrictRuns = $null
    if ($perfSqliteOld) {
        $perfStrictRuns = @(
            [pscustomobject]@{ Exe = $perfNew; Label = 'SQLite-current-strict'; Fixture = $perfFixture },
            [pscustomobject]@{ Exe = $perfSqliteOld; Label = 'SQLite-baseline'; Fixture = $perfSqliteBaselineFixture }
        )
    }
    if ($perfCount -eq 100000) {
        $perfRuns = @($perfRuns[1], $perfRuns[0])
        if ($perfStrictRuns) { $perfStrictRuns = @($perfStrictRuns[1], $perfStrictRuns[0]) }
    }
    foreach ($perfRun in $perfRuns) {
        Run-PerformanceProbe $perfRun.Exe @('run', $perfRun.Fixture, "$perfCount", ('label=' + $perfRun.Label))
    }
    if ($perfStrictRuns) {
        foreach ($perfRun in $perfStrictRuns) {
            Run-PerformanceProbe $perfRun.Exe @('run', $perfRun.Fixture, "$perfCount", ('label=' + $perfRun.Label))
        }
    }
}
# Dense relation metadata is a separate workload; do not hide compatibility-layer costs.
$perfDense = Join-Path $perfRoot 'dense-10000'
Write-Output 'DENSE_RELATIONS=10000'
Run-PerformanceProbe $perfNew @('generate', $perfDense, '10000', 'dense')
Run-PerformanceProbe $perfNew @('migrate', $perfDense, '10000')
$perfDenseSqliteBaseline = $null
if ($perfSqliteOld) {
    $perfDenseSqliteBaseline = Join-Path $perfRoot 'dense-10000-sqlite-baseline'
    [IO.Directory]::CreateDirectory($perfDenseSqliteBaseline) | Out-Null
    Copy-Item -LiteralPath (Join-Path $perfDense 'config.xml') -Destination (Join-Path $perfDenseSqliteBaseline 'config.xml')
    Run-PerformanceProbe $perfSqliteOld @('migrate', $perfDenseSqliteBaseline, '10000')
}
foreach ($perfRun in @(
    [pscustomobject]@{ Exe = $perfOld; Label = 'XML-baseline'; Fixture = $perfDense },
    [pscustomobject]@{ Exe = $perfNew; Label = 'SQLite-current'; Fixture = $perfDense })) {
    Run-PerformanceProbe $perfRun.Exe @('run', $perfRun.Fixture, '10000', ('label=' + $perfRun.Label))
}
if ($perfDenseSqliteBaseline) {
    foreach ($perfRun in @(
        [pscustomobject]@{ Exe = $perfNew; Label = 'SQLite-current-strict'; Fixture = $perfDense },
        [pscustomobject]@{ Exe = $perfSqliteOld; Label = 'SQLite-baseline'; Fixture = $perfDenseSqliteBaseline })) {
        Run-PerformanceProbe $perfRun.Exe @('run', $perfRun.Fixture, '10000', ('label=' + $perfRun.Label))
    }
}
if ($LiveDatabasePath) {
    $perfUser = Join-Path $perfRoot 'current-user'
    $perfSnapshot = @(Run-PerformanceProbe $perfNew @('snapshot', $perfUser, '0', [IO.Path]::GetFullPath($LiveDatabasePath)))
    $perfSnapshot | Write-Output
    $perfUserCount = ($perfSnapshot | Where-Object { $_ -like 'SNAPSHOT count=*' } | Select-Object -Last 1).Split('=')[1]
    foreach ($perfRun in @(
        [pscustomobject]@{ Exe = $perfOld; Label = 'XML-baseline' },
        [pscustomobject]@{ Exe = $perfNew; Label = 'SQLite-current' })) {
        Run-PerformanceProbe $perfRun.Exe @('user', $perfUser, "$perfUserCount", ('label=' + $perfRun.Label))
    }
}
Write-Output "DONE results=$perfLog"
