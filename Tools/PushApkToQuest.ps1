<#
.SYNOPSIS
    Copies the built QuestSaber APK into the Download folder of a connected Quest.

.DESCRIPTION
    Works over MTP through the Windows shell namespace, so it needs neither ADB nor
    developer mode - exactly the way the APK is copied by hand with Windows Explorer.

    NOTHING on the headset is ever deleted or overwritten. Over MTP both need a
    confirmation dialog the player would have to click by hand, so instead every push
    gets its own name: QuestSaber-<version>.apk, then QuestSaber-<version>-2.apk, -3, ...
    The HIGHEST number is always the newest build.

    After the copy the remote file size is verified against the local file.

    Exit codes: 0 = copied + verified, 1 = no headset connected (not an error for
    automated builds), 2 = copy/verification failed or no free name, 3 = local APK missing.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\PushApkToQuest.ps1
#>
[CmdletBinding()]
param(
    # Local APK. Default: <project>\Builds\QuestSaber.apk
    [string]$ApkPath,
    # Name fragment of the device in "This PC" (the Quest shows up as "Quest 3").
    [string]$DeviceNameMatch = 'Quest',
    # Remote file name base. Default: QuestSaber-<bundleVersion>.apk
    [string]$RemoteName,
    # How long to wait for the copy to show up (MTP of ~120 MB can take a while).
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Say  { param($m) Write-Host "[push] $m" }
function Warn { param($m) Write-Host "[push] WARN: $m" -ForegroundColor Yellow }

# ---------------------------------------------------------------- local APK
# Default: the newest version-stamped APK in Builds\ (the only APK that lives there;
# older builds are in Builds\archive\).
if (-not $ApkPath) {
    $cand = Get-ChildItem -LiteralPath (Join-Path $root 'Builds') -File -Filter 'QuestSaber-*.apk' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($cand) { $ApkPath = $cand.FullName }
    else       { $ApkPath = Join-Path $root 'Builds\QuestSaber-0.0.apk' }   # -> "not found" below
}
$apk = Get-Item -LiteralPath $ApkPath -ErrorAction SilentlyContinue
if (-not $apk) {
    Warn "local APK not found: $ApkPath"
    exit 3
}
$localSize = $apk.Length
Say ("source : {0} ({1:N0} bytes)" -f $apk.FullName, $localSize)

# ---------------------------------------------------------------- versioned name
if (-not $RemoteName) {
    $ps = Join-Path $root 'ProjectSettings\ProjectSettings.asset'
    $ver = 'dev'
    if (Test-Path -LiteralPath $ps) {
        $m = Select-String -LiteralPath $ps -Pattern '^\s*bundleVersion:\s*(.+)$'
        if ($m) { $ver = $m.Matches[0].Groups[1].Value.Trim() }
    }
    $RemoteName = "QuestSaber-$ver.apk"
}
if ($RemoteName -notlike '*.apk') { $RemoteName += '.apk' }
Say ("target : Download/{0} (renamed if that name is taken)" -f $RemoteName)

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

# Interner gemeinsamer Speicher -> Download
$storage = $null
foreach ($c in $device.GetFolder.Items()) {
    if ($c.Name -match 'shared|gemeinsamer|internal') { $storage = $c; break }
}
if (-not $storage) { $storage = $device }
$download = $null
foreach ($c in $storage.GetFolder.Items()) {
    if ($c.Name -ieq 'Download') { $download = $c; break }
}
if (-not $download) {
    Warn "Download folder not found."
    exit 1
}
Say "folder : Download"

function Get-RemoteItem {
    param($folder, [string]$name)
    try {
        foreach ($i in $folder.GetFolder.Items()) {
            if ($i.Name -eq $name -and -not $i.IsFolder) { return $i }
        }
    } catch { }
    return $null
}
function Get-RemoteSize {
    param($folder, [string]$name)
    $i = Get-RemoteItem $folder $name
    if (-not $i) { return -1 }
    try {
        $s = $i.ExtendedProperty('System.Size')
        if ($null -ne $s) { return [int64]$s }
    } catch { }
    try { return [int64]$i.Size } catch { }
    return -1
}

# ------------------------------------------------- pick a FREE name, never delete
# Deleting or overwriting over MTP needs a confirmation dialog - never do that.
# Every push therefore gets its own file; the highest number is the newest build.
$stem = [System.IO.Path]::GetFileNameWithoutExtension($RemoteName)
for ($n = 1; $n -le 99; $n++) {
    $candidate = if ($n -eq 1) { "$stem.apk" } else { "$stem-$n.apk" }
    if (-not (Get-RemoteItem $download $candidate)) {
        $RemoteName = $candidate
        break
    }
    if ($n -eq 99) {
        Warn "no free name left in Download - aborting so nothing is overwritten."
        exit 2
    }
}
if ($n -gt 1) {
    Say ("renamed : existing file kept -> Download/{0}" -f $RemoteName)
}

if ((Get-RemoteSize $download $RemoteName) -eq $localSize) {
    Say "done   : identical file already present, nothing to copy."
    exit 0
}

# ---------------------------------------------------------------- stage + copy
$stage = Join-Path $env:TEMP ("questsaber_push_" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    Copy-Item -LiteralPath $apk.FullName -Destination (Join-Path $stage $RemoteName) -Force
    $srcNs = $shell.NameSpace($stage)
    $item = $srcNs.ParseName($RemoteName)
    if (-not $item) { Warn "staged item not found in shell namespace."; exit 2 }

    Say "copying ..."
    $sw = [Diagnostics.Stopwatch]::StartNew()
    # 4 no progress UI + 16 yes-to-all + 512 + 1024 no failure UI
    try {
        # $download is a Shell FolderItem - CopyHere is a Folder method.
    $target = if ($download.IsFolder) { $download.GetFolder } else { $download }
    $target.CopyHere($item, 1556)
    } catch {
        Warn ("CopyHere failed: {0}" -f $_.Exception.Message)
        exit 2
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $size = -1
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $size = Get-RemoteSize $download $RemoteName
        if ($size -eq $localSize) { break }
    }
    $sw.Stop()

    if ($size -ne $localSize) {
        Warn ("copy could not be verified: remote {0} vs local {1} bytes after {2:N0}s" -f `
              $size, $localSize, $sw.Elapsed.TotalSeconds)
        exit 2
    }
    Say ("done   : Download/{0} verified ({1:N0} bytes, {2:N0}s)" -f `
         $RemoteName, $size, $sw.Elapsed.TotalSeconds)
    exit 0
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
