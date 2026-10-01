# make-testfiles.ps1 - creates the spike's test file set in <repo>\testfiles (gitignored).
# Works in Windows PowerShell 5.1 and PowerShell 7. Needs no admin rights.
# Idempotent: re-running removes the deny ACE on denied.bin before recreating things.
#
# Also copies the markdown corpus (testdata\markdown) to <Target>\markdown and generates
# big-5mb.md and huge-40mb.md there; -MarkdownOnly stops after that (no hex test files).
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts\make-testfiles.ps1 [-Target <dir>] [-SkipHuge] [-MarkdownOnly]
[CmdletBinding()]
param(
    [string]$Target = '',
    [switch]$SkipHuge,
    [switch]$MarkdownOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Target) { $Target = Join-Path (Split-Path -Parent $ScriptDir) 'testfiles' }

function Write-RandomFile([string]$Path, [long]$Size) {
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $chunk = 4MB
    $buf = New-Object byte[] ([Math]::Min($chunk, [Math]::Max($Size, 1)))
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $left = $Size
        while ($left -gt 0) {
            $n = [int][Math]::Min([long]$buf.Length, $left)
            $rng.GetBytes($buf)
            $fs.Write($buf, 0, $n)
            $left -= $n
        }
    } finally { $fs.Dispose(); $rng.Dispose() }
}

$Target = [System.IO.Path]::GetFullPath($Target)
New-Item -ItemType Directory -Force -Path $Target | Out-Null
Write-Host "Test files -> $Target"

# --- markdown\: committed corpus + generated big files ----------------------------
# Copies testdata\markdown\*.md byte for byte (BOM, UTF-16, CRLF files must stay as they are)
# and generates two deterministic large files from readme.md:
#   big-5mb.md    readme.md repeated until >= 5 MB, headings numbered per copy ("## 12. Features")
#   huge-40mb.md  the same until >= 40 MB (exceeds the plugin's 32 MB read cap)
function Write-RepeatedMarkdown([string]$Source, [string]$Path, [long]$MinBytes) {
    $lines = [System.IO.File]::ReadAllLines($Source, [System.Text.Encoding]::UTF8)
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $sw = New-Object System.IO.StreamWriter($Path, $false, $utf8, 1MB)
    try {
        $sw.NewLine = "`n"
        $copy = 0
        while ($sw.BaseStream.Length -lt $MinBytes) {
            $copy++
            $inFence = $false
            foreach ($line in $lines) {
                if ($line -match '^\s{0,3}(```|~~~)') { $inFence = -not $inFence; $sw.WriteLine($line); continue }
                if (-not $inFence -and $line -match '^(#{1,6}) (.*)$') { $sw.WriteLine("$($Matches[1]) $copy. $($Matches[2])"); continue }
                $sw.WriteLine($line)
            }
            $sw.WriteLine('')
            $sw.Flush()
        }
    } finally { $sw.Dispose() }
}

$mdSource = Join-Path (Split-Path -Parent $ScriptDir) 'testdata\markdown'
$mdTarget = Join-Path $Target 'markdown'
New-Item -ItemType Directory -Force -Path $mdTarget | Out-Null
Copy-Item -Path (Join-Path $mdSource '*.md') -Destination $mdTarget -Force
$readme = Join-Path $mdSource 'readme.md'
Write-RepeatedMarkdown $readme (Join-Path $mdTarget 'big-5mb.md') (5MB)
Write-RepeatedMarkdown $readme (Join-Path $mdTarget 'huge-40mb.md') (40MB)
Write-Host ("markdown\: {0} files" -f @(Get-ChildItem -LiteralPath $mdTarget -File).Count)
if ($MarkdownOnly) {
    Get-ChildItem -LiteralPath $mdTarget -File | Format-Table Name, Length -AutoSize | Out-Host
    Write-Host 'Done (markdown only).'
    return
}

# --- disk space check ----------------------------------------------------------
$drive = New-Object System.IO.DriveInfo ([System.IO.Path]::GetPathRoot($Target))
$freeGB = [Math]::Round($drive.AvailableFreeSpace / 1GB, 1)
Write-Host "Free space on $($drive.Name): $freeGB GB"
$hugeSize = [long](4.5 * 1GB)
$hugePath = Join-Path $Target 'huge.bin'
$needHuge = -not $SkipHuge -and -not ((Test-Path -LiteralPath $hugePath) -and ((Get-Item -LiteralPath $hugePath).Length -eq $hugeSize))
if ($needHuge -and $drive.AvailableFreeSpace -lt ($hugeSize + 1GB)) {
    throw "Not enough free space for huge.bin (need ~5.5 GB, have $freeGB GB). Use -SkipHuge or free space."
}

# --- current user SID (works for local, domain and AzureAD accounts) ---------
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value

# --- simple files -------------------------------------------------------------
[System.IO.File]::WriteAllBytes((Join-Path $Target 'empty.bin'), [byte[]]@())
[System.IO.File]::WriteAllBytes((Join-Path $Target 'one.bin'), [byte[]]@(0x41))
Write-RandomFile (Join-Path $Target 'small.bin') 1KB
$medPath = Join-Path $Target 'medium.bin'
if (-not ((Test-Path -LiteralPath $medPath) -and ((Get-Item -LiteralPath $medPath).Length -eq 100MB))) {
    Write-Host 'Writing medium.bin (100 MB random)...'
    Write-RandomFile $medPath 100MB
}
if ($needHuge) {
    if (Test-Path -LiteralPath $hugePath) { Remove-Item -LiteralPath $hugePath -Force }
    Write-Host 'Creating huge.bin (4.5 GB, fsutil)...'
    & fsutil file createnew $hugePath $hugeSize | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "fsutil failed ($LASTEXITCODE)" }
    # fsutil creates an all-zero file. Put a recognizable marker at the start and end so the view is not just zeros.
    $fs = [System.IO.File]::Open($hugePath, 'Open', 'Write', 'None')
    try {
        $m = [System.Text.Encoding]::ASCII.GetBytes('HUGE.BIN START MARKER 0123456789')
        $fs.Write($m, 0, $m.Length)
        $fs.Seek(-$m.Length, 'End') | Out-Null
        $m = [System.Text.Encoding]::ASCII.GetBytes('HUGE.BIN END MARKER   0123456789')
        $fs.Write($m, 0, $m.Length)
    } finally { $fs.Dispose() }
}

# locked.bin: plain content. Locking happens at test time (harness, or scripts\lock-file.ps1 for manual checks).
Write-RandomFile (Join-Path $Target 'locked.bin') 4KB
# __throw__.bin: any content; the throw-test build of the plugin throws when it sees this name.
Write-RandomFile (Join-Path $Target '__throw__.bin') 1KB

# --- denied.bin: ACL deny read for the current user ----------------------------
$deniedPath = Join-Path $Target 'denied.bin'
if (Test-Path -LiteralPath $deniedPath) {
    & icacls $deniedPath /remove:d "*$sid" | Out-Null
}
Write-RandomFile $deniedPath 4KB
& icacls $deniedPath /deny "*${sid}:(R)" | Out-Host
if ($LASTEXITCODE -ne 0) { throw "icacls /deny failed ($LASTEXITCODE)" }

# --- non-ASCII name: "naïve – 日本語.bin" (built from code points so file encoding cannot break it) ---
$uniName = 'na' + [char]0x00EF + 've ' + [char]0x2013 + ' ' + [char]0x65E5 + [char]0x672C + [char]0x8A9E + '.bin'
Write-RandomFile (Join-Path $Target $uniName) 2KB

# --- long path (> 260 chars) via nested folders, created with \\?\ paths ----------
$longRoot = Join-Path $Target 'longpath'
$p = $longRoot
$seg = 0
while (($p.Length + 20) -le 300) {
    $seg++
    $p = Join-Path $p ('level{0:D2}_{1}' -f $seg, ('x' * 40))
}
$longFile = Join-Path $p 'long.bin'
[System.IO.Directory]::CreateDirectory('\\?\' + $p) | Out-Null
$fs = [System.IO.File]::Open('\\?\' + $longFile, 'Create', 'Write', 'None')
try { $b = New-Object byte[] 2048; (New-Object Random 42).NextBytes($b); $fs.Write($b, 0, $b.Length) } finally { $fs.Dispose() }
Write-Host ("Long path ({0} chars): {1}" -f $longFile.Length, $longFile)

# --- mixed\: 200 real files copied from this machine ------------------------------
$mixed = Join-Path $Target 'mixed'
if (Test-Path -LiteralPath $mixed) { Remove-Item -LiteralPath $mixed -Recurse -Force }
New-Item -ItemType Directory -Force -Path $mixed | Out-Null
$repo = Split-Path -Parent $ScriptDir
$sources = @(
    @{ Path = "$env:WINDIR\Web";              Filter = '*';     Recurse = $true;  Take = 40 },
    @{ Path = "$env:WINDIR\System32";         Filter = '*.txt'; Recurse = $false; Take = 15 },
    @{ Path = "$env:WINDIR\System32";         Filter = '*.ini'; Recurse = $false; Take = 15 },
    @{ Path = "$env:WINDIR\System32";         Filter = '*.xml'; Recurse = $false; Take = 15 },
    @{ Path = "$env:WINDIR\System32";         Filter = '*.dll'; Recurse = $false; Take = 15 },
    @{ Path = "$env:WINDIR\Media";            Filter = '*';     Recurse = $false; Take = 20 },
    @{ Path = "$env:ProgramFiles\dotnet";     Filter = '*';     Recurse = $true;  Take = 60 },
    @{ Path = "$repo\docs";                   Filter = '*';     Recurse = $true;  Take = 10 },
    @{ Path = "$env:WINDIR";                  Filter = '*.*';   Recurse = $false; Take = 40 },
    # fallback filler so the folder reaches 200 files
    @{ Path = "$env:WINDIR\System32\drivers\etc"; Filter = '*'; Recurse = $false; Take = 10 },
    @{ Path = "$env:WINDIR\System32";         Filter = '*.*';   Recurse = $false; Take = 120 }
)
$picked = New-Object System.Collections.Generic.List[System.IO.FileInfo]
foreach ($s in $sources) {
    if (-not (Test-Path -LiteralPath $s.Path)) { continue }
    $files = @(Get-ChildItem -LiteralPath $s.Path -Filter $s.Filter -File -Recurse:$s.Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -le 20MB } | Select-Object -First ($s.Take * 4))
    # spread the picks across the listing for variety
    $step = [Math]::Max(1, [int][Math]::Floor($files.Count / [Math]::Max(1, $s.Take)))
    for ($i = 0; $i -lt $files.Count -and ($i / $step) -lt $s.Take; $i += $step) { $picked.Add($files[$i]) }
}
$n = 0
foreach ($f in $picked) {
    if ($n -ge 200) { break }
    $dest = Join-Path $mixed ('{0:D3}_{1}' -f $n, $f.Name)
    try { Copy-Item -LiteralPath $f.FullName -Destination $dest -ErrorAction Stop; $n++ } catch { }
}
Write-Host "mixed\: $n files"
if ($n -lt 200) { Write-Warning "Only $n readable files found for mixed\ (wanted 200)." }

Write-Host ''
Get-ChildItem -LiteralPath $Target -File | Format-Table Name, Length -AutoSize | Out-Host
Write-Host 'Done.'
