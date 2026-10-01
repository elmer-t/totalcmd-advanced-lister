# run-harness.ps1 - builds the native harness (if needed) and runs it from the repo root.
# Any extra arguments are passed to Harness.exe, e.g.:
#   powershell -ExecutionPolicy Bypass -File scripts\run-harness.ps1 --launches 5 --phases loadtime,edge
# Defaults: --dll out\AdvancedViewer.wlx64 --throwdll out\throwtest\AdvancedViewer.wlx64 --files testfiles --json out\harness-results.json
# Phases (default all): loadtime,perfile,cycle,scroll,markdown,edge,throw,unload.
#   markdown reads testfiles\markdown\*.md (incl. big-5mb.md, huge-40mb.md), e.g.:
#   powershell -ExecutionPolicy Bypass -File scripts\run-harness.ps1 --phases markdown
# Visual check of one file in a 1000x800 window (in-process; close it or press Esc to exit):
#   powershell -ExecutionPolicy Bypass -File scripts\run-harness.ps1 --show testfiles\markdown\readme.md [--dark]
param([switch]$Rebuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $repo 'src\Harness\bin\Harness.exe'
$src = Join-Path $repo 'src\Harness\harness.cpp'
if ($Rebuild -or -not (Test-Path $exe) -or (Get-Item $src).LastWriteTime -gt (Get-Item $exe).LastWriteTime) {
    & cmd /c (Join-Path $repo 'src\Harness\build.cmd')
    if ($LASTEXITCODE -ne 0) { throw "Harness build failed ($LASTEXITCODE)" }
}
if (-not (Test-Path (Join-Path $repo 'testfiles\small.bin'))) {
    Write-Warning 'testfiles\ missing - run scripts\make-testfiles.ps1 first.'
}
Push-Location $repo
try {
    & $exe @args
    $code = $LASTEXITCODE
} finally { Pop-Location }
exit $code
