# fetch-libmpv.ps1 - download the libmpv runtime used by the in-app video player.
# ASCII only: Windows PowerShell 5.1 reads scripts with the system code page,
# so non-ASCII characters would be garbled on some machines.
#
# The DLL is not committed because it is a large binary (about 112 MiB). This
# script downloads a pinned NuGet package from nuget.org, verifies the package
# and the DLL by SHA256, then places libmpv-2.dll next to AppManager.exe.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\fetch-libmpv.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\fetch-libmpv.ps1 -Force
#   ... -Destination C:\path\libmpv-2.dll
param(
    [string]$Destination = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $runtimeRoot = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "AppManager"
    $Destination = Join-Path $runtimeRoot "libmpv-2.dll"
}
$destinationDir = Split-Path -Parent $Destination
if ((-not [string]::IsNullOrEmpty($destinationDir)) -and (-not (Test-Path $destinationDir))) {
    New-Item -ItemType Directory -Force $destinationDir | Out-Null
}
$license = Join-Path (Split-Path -Parent $PSScriptRoot) "libmpv-LICENSE.txt"
if ((Test-Path -LiteralPath $license) -and (-not [string]::IsNullOrEmpty($destinationDir))) {
    Copy-Item -LiteralPath $license -Destination (Join-Path $destinationDir "libmpv-LICENSE.txt") -Force
}

if ((Test-Path $Destination) -and (-not $Force)) {
    Write-Host "libmpv-2.dll already exists: $Destination"
    Write-Host "Use -Force to download and replace it."
    exit 0
}

# Pinned package: Endpne.LibMPV.Windows 0.41.0 contains build\x64\libmpv-2.dll.
$packageId  = "endpne.libmpv.windows"
$packageVer = "0.41.0"
$packageUrl = "https://api.nuget.org/v3-flatcontainer/$packageId/$packageVer/$packageId.$packageVer.nupkg"
$packageSha256 = "99811FA17D8D14C64C5F35A230D8F77FAAB63AAA85097F67D272F781E677C252"
$dllEntryPath  = "build/x64/libmpv-2.dll"
$dllSha256     = "38ACD030006062830D792A958C5F2ADC293FA965996797980EC73D9610C309F4"

$tempDir = Join-Path $env:TEMP ("appmanager-libmpv-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $tempDir | Out-Null
Write-Host "Downloading libmpv package (about 78 MiB)..."
Write-Host "  $packageUrl"

try {
    $package = Join-Path $tempDir "libmpv.nupkg"
    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        & curl.exe -L --retry 3 --connect-timeout 20 --max-time 900 -o $package $packageUrl
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $package)) {
            throw "curl failed with exit code $LASTEXITCODE"
        }
    } else {
        Invoke-WebRequest -Uri $packageUrl -OutFile $package -UseBasicParsing
    }

    $hash = (Get-FileHash $package -Algorithm SHA256).Hash
    if ($hash -ne $packageSha256) {
        throw "Package SHA256 mismatch. Expected $packageSha256, got $hash"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq $dllEntryPath } | Select-Object -First 1
        if (-not $entry) {
            throw "Entry $dllEntryPath not found in package"
        }
        $dll = Join-Path $tempDir "libmpv-2.dll"
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dll, $true)
    } finally {
        $zip.Dispose()
    }

    $dllHash = (Get-FileHash $dll -Algorithm SHA256).Hash
    if ($dllHash -ne $dllSha256) {
        throw "DLL SHA256 mismatch. Expected $dllSha256, got $dllHash"
    }

    Copy-Item $dll $Destination -Force
    Write-Host ""
    Write-Host "Installed: $Destination"
    Write-Host "SHA256   : $dllHash"
    Write-Host "libmpv is licensed under LGPL; libmpv-LICENSE.txt was installed next to the DLL."
    exit 0
}
catch {
    Write-Error ("libmpv fetch failed: " + $_.Exception.Message)
    Write-Host "You can download the package manually from:"
    Write-Host "  https://www.nuget.org/packages/$packageId/$packageVer"
    Write-Host "Rename it to .zip, extract $dllEntryPath, and place it next to AppManager.exe."
    exit 1
}
finally {
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
