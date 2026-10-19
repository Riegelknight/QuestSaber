<#
.SYNOPSIS
    Pulls the current debug log of a connected Quest into <project>\Game-Logs\.

.DESCRIPTION
    Works over MTP through the Windows shell namespace, so it needs neither ADB nor
    developer mode - exactly the way a file is copied out with Windows Explorer.

    Game-Logs\ always holds the NEWEST headset log: the local copy is replaced on every
    run. Only the local copy is written - the file on the headset is READ, never
    modified, moved or deleted.

    Sources (first hit wins, tried in order):
      1. Download/spiel_debug_log.txt                 - primary, written as soon as
                                                        "All files access" is granted.
      2. Android/data/<pkg>/files/spiel_debug_log.txt - the app-private fallback used
                                                        before that grant. Many MTP
                                                        implementations hide this folder;
                                                        if it is not visible, play one
                                                        session with the grant and retry.

    Exit codes: 0 = fetched + verified, 1 = no headset or no log found (not an error for
    an automated run), 2 = copy could not be verified.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\FetchLogFromQuest.ps1
#>
[CmdletBinding()]
param(
    # Name fragment of the device in "This PC" (the Quest shows up as "Quest 3").
    [string]$DeviceNameMatch = 'Quest',
    # Android package of the game (used for the app-private fallback path).
    [string]$PackageName = 'com.questsaber.questsaber',
    # Remote file name of the log.
    [string]$RemoteName = 'spiel_debug_log.txt',
    # Where the newest copy lands. Default: <project>\Game-Logs\
    [string]$TargetDir,
    # How long to wait for the copy to arrive (the log is a few MB at most).
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Say  { param($m) Write-Host "[fetch] $m" }
function Warn { param($m) Write-Host "[fetch] WARN: $m" -ForegroundColor Yellow }

if (-not $TargetDir) { $TargetDir = Join-Path $root 'Game-Logs' }
New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

# ---------------------------------------------------------------- locate the device
$shell = New-Object -ComObject Shell.Application
$pc = $shell.NameSpace(0x11)   # This PC / Der PC
if (-not $pc) { Warn "Shell:This PC not available."; exit 1 }

$device = $null
foreach ($d in $pc.Items()) {
    if ($d.Name -like "*$DeviceNameMatch*") { $device = $d; break }
}
if (-not $device) {
    Warn "no device matching '$DeviceNameMatch' is connected."
    exit 1
}
Say ("device : {0}" -f $device.Name)

# Internal shared storage (English/German/...) -> the storage root.
$storage = $null
foreach ($c in $device.GetFolder.Items()) {
    if ($c.Name -match 'shared|gemeinsamer|internal') { $storage = $c; break }
}
if (-not $storage) { $storage = $device }

function Get-ChildFolder {
    param($parent, [string]$name)
    try {
        foreach ($i in $parent.GetFolder.Items()) {
            if ($i.IsFolder -and $i.Name -ieq $name) { return $i }
        }
    } catch { }
    return $null
}

function Get-FileItem {
    param($folder, [string]$name)
    try {
        foreach ($i in $folder.GetFolder.Items()) {
            if (-not $i.IsFolder -and $i.Name -eq $name) { return $i }
        }
    } catch { }
    return $null
}

function Get-ItemSize {
    param($item)
    try {
        $s = $item.ExtendedProperty('System.Size')
        if ($null -ne $s) { return [int64]$s }
    } catch { }
    try { return [int64]$item.Size } catch { }
    return -1
}

# ---------------------------------------------------------------- find the log
# 1) Download/...  2) Android/media/<pkg>/...  3) Android/data/<pkg>/files/... (may be hidden over MTP)
$remoteItem = $null
$remoteWhere = ''

$download = Get-ChildFolder $storage 'Download'
if ($download) {
    $remoteItem = Get-FileItem $download $RemoteName
    if ($remoteItem) { $remoteWhere = "Download/$RemoteName" }
}

if (-not $remoteItem) {
    $android  = Get-ChildFolder $storage 'Android'
    $media    = if ($android) { Get-ChildFolder $android 'media' } else { $null }
    $mediaPkg = if ($media)   { Get-ChildFolder $media $PackageName } else { $null }
    if ($mediaPkg) {
        $remoteItem = Get-FileItem $mediaPkg $RemoteName
        if ($remoteItem) { $remoteWhere = "Android/media/$PackageName/$RemoteName" }
    }
}

if (-not $remoteItem) {
    $android = Get-ChildFolder $storage 'Android'
    $data    = if ($android) { Get-ChildFolder $android 'data' }  else { $null }
    $pkg     = if ($data)    { Get-ChildFolder $data $PackageName } else { $null }
    $files   = if ($pkg)     { Get-ChildFolder $pkg 'files' }       else { $null }
    if ($files) {
        $remoteItem = Get-FileItem $files $RemoteName
        if ($remoteItem) { $remoteWhere = "Android/data/$PackageName/files/$RemoteName" }
    }
}

if (-not $remoteItem) {
    Warn "no $RemoteName on the headset (Download/ and Android/media/ not readable and the app-private"
    Warn "folder is hidden over MTP). Play one session with 'All files access' granted, then retry."
    exit 1
}

$remoteSize = Get-ItemSize $remoteItem
if ($remoteSize -lt 0) {
    Warn "could not read the size of the remote log."
    exit 2
}
Say ("source : {0} ({1:N0} bytes)" -f $remoteWhere, $remoteSize)

# ---------------------------------------------------------------- copy (local only)
$localPath = Join-Path $TargetDir $RemoteName

# Replacing the LOCAL file is what makes Game-Logs always show the newest session.
# Nothing on the headset is touched.
if (Test-Path -LiteralPath $localPath) {
    Remove-Item -LiteralPath $localPath -Force -ErrorAction Stop
}

$localNs = $shell.NameSpace($TargetDir)
if (-not $localNs) { Warn "cannot open target folder: $TargetDir"; exit 2 }

$sw = [Diagnostics.Stopwatch]::StartNew()
# 4 no progress UI + 16 yes-to-all + 512 + 1024 no failure UI (same flags as the push script)
try {
    $localNs.CopyHere($remoteItem, 1556)
} catch {
    Warn ("CopyHere failed: {0}" -f $_.Exception.Message)
    exit 2
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$localSize = -1
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $item = Get-Item -LiteralPath $localPath -ErrorAction SilentlyContinue
    if ($item) { $localSize = $item.Length; if ($localSize -eq $remoteSize) { break } }
}
$sw.Stop()

if ($localSize -ne $remoteSize) {
    Warn ("copy could not be verified: local {0} vs remote {1} bytes after {2:N0}s" -f `
          $localSize, $remoteSize, $sw.Elapsed.TotalSeconds)
    exit 2
}

Say ("done   : {0} ({1:N0} bytes, {2:N1}s)" -f $localPath, $localSize, $sw.Elapsed.TotalSeconds)

# ---------------------------------------------------------------- quick peek
# The header identifies the build that produced the log - that is the first thing to
# check when a log and an APK do not match. Best effort only.
try {
    $head = Get-Content -LiteralPath $localPath -TotalCount 14
    $stamp = $head | Where-Object { $_ -match 'Build\s+:' } | Select-Object -First 1
    if ($stamp) { Say ("build  : {0}" -f $stamp.Trim()) }
    $ver = $head | Where-Object { $_ -match 'App version' } | Select-Object -First 1
    if ($ver) { Say ("version: {0}" -f $ver.Trim()) }
} catch { }
exit 0
