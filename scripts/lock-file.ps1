# lock-file.ps1 - holds a file open with no sharing until Enter is pressed (for the manual locked.bin check).
# Usage: powershell -ExecutionPolicy Bypass -File scripts\lock-file.ps1 [-Path testfiles\locked.bin]
param([string]$Path = '')
if (-not $Path) { $Path = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'testfiles\locked.bin' }
$ErrorActionPreference = 'Stop'
$full = [System.IO.Path]::GetFullPath($Path)
$fs = [System.IO.File]::Open($full, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
try {
    Write-Host "Locked (no sharing): $full"
    Read-Host 'Press Enter to release the lock'
} finally { $fs.Dispose(); Write-Host 'Released.' }
