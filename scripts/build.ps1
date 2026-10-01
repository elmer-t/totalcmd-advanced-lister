<#
.SYNOPSIS
  Builds the Advanced Viewer Lister plugin with Native AOT and packages it for Total Commander.

.DESCRIPTION
  Produces, under <repo>\out\:
    AdvancedViewer.wlx64             Release build (the plugin to install / measure)
    throwtest\AdvancedViewer.wlx64   Release build compiled with SPIKE_THROW: opening a file named
                                     __throw__.bin throws inside ListLoadW/ListLoad/ListLoadNextW/ListLoadNext,
                                     so the export catch-all can be tested (harness step 7)
    AdvancedViewer-plugin.zip        AdvancedViewer.wlx64 + pluginst.inf (open in TC to auto-install)
    pluginst.inf
    dumpbin-exports.txt              dumpbin /exports of out\AdvancedViewer.wlx64
    build-release.log, build-throwtest.log   full publish output

  Fails if the publish fails or prints any warning (IL2xxx/IL3xxx are errors in the csproj anyway).

.PARAMETER Clean
  Delete src\AdvancedViewer\bin and obj first (full rebuild, including the ILC native compile).
#>
[CmdletBinding()]
param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo    = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\AdvancedViewer\AdvancedViewer.csproj'
$out     = Join-Path $repo 'out'

# --- Visual Studio / MSVC linker -------------------------------------------------------------
# Native AOT links with MSVC link.exe, located through vswhere + vcvarsall by the ILCompiler
# targets. VsDevCmd.bat runs "vswhere.exe" from a pushd'd directory, which fails when
# NoDefaultCurrentDirectoryInExePath is set (some shells/agents set it). Put the vswhere
# directory on PATH so that lookup works regardless.
$pf86 = [Environment]::GetFolderPath('ProgramFilesX86')
$vswhere = Join-Path $pf86 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found at $vswhere. Install Visual Studio 2022+ with 'Desktop development with C++'." }
$env:PATH = (Split-Path -Parent $vswhere) + ';' + $env:PATH

$vsPath = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw 'No Visual Studio installation with the C++ x64 tools found (needed by Native AOT).' }
$dumpbin = Get-ChildItem -Path (Join-Path $vsPath 'VC\Tools\MSVC') -Filter dumpbin.exe -Recurse |
    Where-Object { $_.FullName -match '\\bin\\Hostx64\\x64\\' } |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $dumpbin) { throw "dumpbin.exe not found under $vsPath" }

# --- Build ---------------------------------------------------------------------------------
if ($Clean) {
    foreach ($d in 'bin', 'obj') {
        $p = Join-Path $repo "src\AdvancedViewer\$d"
        if (Test-Path $p) { Remove-Item -Recurse -Force $p }
    }
}
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Invoke-Publish([string]$name, [string[]]$extraArgs) {
    $pubDir = Join-Path $out "publish\$name"
    $log = Join-Path $out "build-$name.log"
    Write-Host "== dotnet publish ($name)" -ForegroundColor Cyan
    $pubArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '-o', $pubDir, '-nologo', '-clp:Summary') + $extraArgs
    & dotnet @pubArgs 2>&1 | Tee-Object -FilePath $log | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($name) failed with exit code $LASTEXITCODE. See $log" }

    $warnings = @(Select-String -Path $log -Pattern ': warning [A-Z]+[0-9]+' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
    $ilWarnings = @($warnings | Where-Object { $_ -match ': warning IL[23][0-9]{3}' })
    Write-Host ("   warnings: {0} (IL2xxx/IL3xxx: {1})" -f $warnings.Count, $ilWarnings.Count)
    if ($warnings.Count -gt 0) { $warnings | ForEach-Object { Write-Warning $_ }; throw "Build ($name) produced warnings." }

    $dll = Join-Path $pubDir 'AdvancedViewer.dll'
    if (-not (Test-Path $dll)) { throw "Expected $dll was not produced." }
    return $dll
}

$releaseDll   = Invoke-Publish 'release'   @()
$throwtestDll = Invoke-Publish 'throwtest' @('-p:SpikeThrow=true')

# --- Rename to .wlx64 ----------------------------------------------------------------------
$wlx = Join-Path $out 'AdvancedViewer.wlx64'
Copy-Item $releaseDll $wlx -Force
$throwDir = Join-Path $out 'throwtest'
New-Item -ItemType Directory -Force -Path $throwDir | Out-Null
Copy-Item $throwtestDll (Join-Path $throwDir 'AdvancedViewer.wlx64') -Force

# --- pluginst.inf + install zip ------------------------------------------------------------
# Format: TotalcmdWiki "Pluginst.inf" ([plugininstall] section). The TC 11.58 help does not
# document it. For a 64-bit-only plugin the registered file name must end in "64" (Lister SDK,
# "64-bit support"), so file= names the .wlx64 directly. Kept pure ASCII.
$inf = @"
[plugininstall]
description=Advanced Viewer (Markdown and hex view, .NET Native AOT)
type=wlx
file=AdvancedViewer.wlx64
defaultdir=AdvancedViewer
version=0.2.0
"@
$infPath = Join-Path $out 'pluginst.inf'
[IO.File]::WriteAllText($infPath, ($inf -replace "`r?`n", "`r`n") + "`r`n", [Text.Encoding]::ASCII)

$zip = Join-Path $out 'AdvancedViewer-plugin.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $wlx, $infPath -DestinationPath $zip -CompressionLevel Optimal

# --- dumpbin /exports --------------------------------------------------------------------------
$exportsTxt = Join-Path $out 'dumpbin-exports.txt'
& $dumpbin.FullName /nologo /exports $wlx | Out-File -FilePath $exportsTxt -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw "dumpbin failed with exit code $LASTEXITCODE" }

$required = 'ListLoadW', 'ListLoad', 'ListLoadNextW', 'ListLoadNext', 'ListCloseWindow', 'ListGetDetectString', 'ListSetDefaultParams', 'ListSendCommand'
$exportText = Get-Content $exportsTxt -Raw
$missing = @($required | Where-Object { $exportText -notmatch "\b$_\b" })
if ($missing.Count -gt 0) { throw "Missing exports: $($missing -join ', ')" }

# --- Summary -----------------------------------------------------------------------------------
Write-Host ''
Write-Host '== Done' -ForegroundColor Green
foreach ($f in $wlx, (Join-Path $throwDir 'AdvancedViewer.wlx64'), $zip, $exportsTxt) {
    $i = Get-Item $f
    Write-Host ("   {0,-45} {1,12:N0} bytes" -f $i.FullName.Substring($repo.Length + 1), $i.Length)
}
Write-Host "   exports: $($required -join ', ') (all present)"
