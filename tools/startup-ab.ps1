# Copyright (c) 2026 Morgott. Licensed under CC BY-NC 4.0 (see LICENSE).
#
# Menu-ready time of the game with the modpack bundle cache, the local cache only, or no cache (cold).
#
#   pwsh -File tools\startup-ab.ps1 -Setup Pack  -PackDir <build-pack-cache.ps1 -OutDir> -Repeat 3 [-Unstamped]
#   pwsh -File tools\startup-ab.ps1 -Setup Local -Repeat 3
#   pwsh -File tools\startup-ab.ps1 -Setup None  -Repeat 3
#
# Pack  = pack copies installed, local cache parked; run #1 is the first launch after a pack update (copies and sources
#         hashed), later runs are stamped (-Unstamped: every run hashes). Local = no pack, the current local cache. None = no pack, empty
#         local cache before each run (the cold 38 s case). Parked folders stay inside BepInEx\Morgott.Preloader and are moved back in
#         finally. Starts only its own valheim.exe, refuses when one from the game folder already runs, closes it at the
#         menu. Holds the machine-wide game slot for the whole run (busy -> exit 2; -SlotWaitSec N waits first).
#         Needs a quiet machine for meaningful numbers.
param(
    [Parameter(Mandatory)][ValidateSet('Pack', 'Local', 'None')][string]$Setup,
    [string]$PackDir,
    [string]$Game = 'D:\Steam\steamapps\common\Valheim',
    [ValidateRange(1, 20)][int]$Repeat = 3,
    [switch]$Unstamped,
    [int]$TimeoutSec = 300,
    # Machine-wide game slot (E:\DEV\Valheim\tools\lib\GameSlot.ps1): wait this long when busy, then exit 2 (0 = refuse at once).
    [ValidateRange(0, 7200)][int]$SlotWaitSec = 0,
    [string]$GameSlotLib = (Join-Path $PSScriptRoot '..\..\tools\lib\GameSlot.ps1')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $GameSlotLib)) { throw "game slot library not found: $GameSlotLib (pass -GameSlotLib)" }
. $GameSlotLib
if ($Setup -eq 'Pack' -and -not (Test-Path -LiteralPath (Join-Path $PackDir 'v1-*\manifest.tsv'))) { throw '-Setup Pack needs -PackDir with v1-<Unity>\manifest.tsv' }
$fs = "$Game\BepInEx\Morgott.Preloader"
$log = "$Game\BepInEx\LogOutput.log"
$local = "$fs\cache\bundles"
$pack = "$fs\pack\bundles"
$parkedLocal = "$fs\ab-parked-local"
$parkedPack = "$fs\ab-parked-pack"
if ((Test-Path $parkedLocal) -or (Test-Path $parkedPack)) { throw "parked folders from an interrupted run exist in $fs - move them back first" }

function Wait-Menu {
    if (Get-Process | Where-Object { $_.Path -like "$Game\*" }) { throw "a valheim.exe from $Game is running - not starting" }
    $start = Get-Date
    $p = Start-Process "$Game\valheim.exe" -ArgumentList '-console' -WorkingDirectory $Game -PassThru
    $line = $null
    try {
        while (-not $line -and ((Get-Date) - $start).TotalSeconds -lt $TimeoutSec -and -not $p.HasExited) {
            Start-Sleep 2
            if ((Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $start) { $line = Select-String $log -Pattern 'menu ready ([\d.,]+) s after process start' | Select-Object -First 1 }
        }
        Start-Sleep 3   # BundleCache summary line is logged right after
    } finally {
        if (-not $p.HasExited) {
            $p.CloseMainWindow() | Out-Null
            if (-not $p.WaitForExit(45000)) { Stop-Process -Id $p.Id -Force }
        }
    }
    $hits = Select-String $log -Pattern 'BundleCache: \d+ pack hits.*' | Select-Object -First 1
    [pscustomobject]@{ MenuReadyS = $line ? $line.Matches[0].Groups[1].Value : 'n/a'; BundleCache = $hits ? $hits.Matches[0].Value : '' }
}

# Held from before the first launch until the last game is closed (busy = lock held or any valheim.exe / valheim_server.exe).
$slot = Enter-GameSlot 'startup-ab.ps1' $SlotWaitSec
if ($slot.Busy) { Write-Host "GAME BUSY: $($slot.Reason)" -ForegroundColor Red; exit 2 }
try {
    if (Test-Path $local) { Move-Item $local $parkedLocal }
    if (Test-Path $pack) { Move-Item $pack $parkedPack }
    if ($Setup -eq 'Pack') { New-Item -ItemType Directory -Force $pack | Out-Null; Copy-Item "$PackDir\*" $pack -Recurse }
    if ($Setup -eq 'Local' -and (Test-Path $parkedLocal)) { Copy-Item $parkedLocal $local -Recurse }
    for ($i = 1; $i -le $Repeat; $i++) {
        if ($Setup -ne 'Local' -and (Test-Path $local)) {
            $keep = Get-ChildItem $local -Recurse -Filter pack-verified.tsv | Where-Object { -not $Unstamped } | Select-Object -First 1
            $stamp = $keep ? (Get-Content -LiteralPath $keep.FullName -Raw) : $null
            Remove-Item $local -Recurse -Force
            if ($stamp) { New-Item -ItemType Directory -Force (Split-Path $keep.FullName) | Out-Null; [IO.File]::WriteAllText($keep.FullName, $stamp) }
        }
        $r = Wait-Menu
        "$Setup#$i menu ready $($r.MenuReadyS) s | $($r.BundleCache)"
    }
} finally {
    if (Test-Path $local) { Remove-Item $local -Recurse -Force }   # test-made (Pack/None) or a copy of the parked cache (Local)
    if (Test-Path $pack) { Remove-Item $pack -Recurse -Force }
    if (Test-Path $parkedLocal) { Move-Item $parkedLocal $local }
    if (Test-Path $parkedPack) { Move-Item $parkedPack $pack }
    Exit-GameSlot $slot
}
