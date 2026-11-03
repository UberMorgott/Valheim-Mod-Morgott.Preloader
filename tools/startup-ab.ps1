# Copyright (c) 2026 Morgott. Licensed under CC BY-NC 4.0 (see LICENSE).
#
# Menu-ready time of the game with the local bundle cache or no cache (cold).
#
#   pwsh -File tools\startup-ab.ps1 -Setup Local -Repeat 3
#   pwsh -File tools\startup-ab.ps1 -Setup None  -Repeat 3
#
# Local = the current local cache. None = empty local cache before each run (the cold 38 s case). The parked cache
#         stays inside BepInEx\Morgott.Preloader and is moved back in finally. Starts only its own valheim.exe, refuses
#         when one from the game folder already runs, closes it at the menu. Holds the machine-wide game slot for the
#         whole run (busy -> exit 2; -SlotWaitSec N waits first). Needs a quiet machine for meaningful numbers.
param(
    [Parameter(Mandatory)][ValidateSet('Local', 'None')][string]$Setup,
    [string]$Game = 'D:\Steam\steamapps\common\Valheim',
    [ValidateRange(1, 20)][int]$Repeat = 3,
    [int]$TimeoutSec = 300,
    # Machine-wide game slot (E:\DEV\Valheim\tools\lib\GameSlot.ps1): wait this long when busy, then exit 2 (0 = refuse at once).
    [ValidateRange(0, 7200)][int]$SlotWaitSec = 0,
    [string]$GameSlotLib = (Join-Path $PSScriptRoot '..\..\tools\lib\GameSlot.ps1')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $GameSlotLib)) { throw "game slot library not found: $GameSlotLib (pass -GameSlotLib)" }
. $GameSlotLib
$fs = "$Game\BepInEx\Morgott.Preloader"
$log = "$Game\BepInEx\LogOutput.log"
$local = "$fs\cache\bundles"
$parkedLocal = "$fs\ab-parked-local"
if (Test-Path $parkedLocal) { throw "parked folder from an interrupted run exists: $parkedLocal - move it back first" }

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
    $hits = Select-String $log -Pattern 'BundleCache: \d+ hits.*' | Select-Object -First 1
    [pscustomobject]@{ MenuReadyS = $line ? $line.Matches[0].Groups[1].Value : 'n/a'; BundleCache = $hits ? $hits.Matches[0].Value : '' }
}

# Held from before the first launch until the last game is closed (busy = lock held or any valheim.exe / valheim_server.exe).
$slot = Enter-GameSlot 'startup-ab.ps1' $SlotWaitSec
if ($slot.Busy) { Write-Host "GAME BUSY: $($slot.Reason)" -ForegroundColor Red; exit 2 }
try {
    if (Test-Path $local) { Move-Item $local $parkedLocal }
    if ($Setup -eq 'Local' -and (Test-Path $parkedLocal)) { Copy-Item $parkedLocal $local -Recurse }
    for ($i = 1; $i -le $Repeat; $i++) {
        if ($Setup -eq 'None' -and (Test-Path $local)) { Remove-Item $local -Recurse -Force }
        $r = Wait-Menu
        "$Setup#$i menu ready $($r.MenuReadyS) s | $($r.BundleCache)"
    }
} finally {
    if (Test-Path $local) { Remove-Item $local -Recurse -Force }   # test-made (None) or a copy of the parked cache (Local)
    if (Test-Path $parkedLocal) { Move-Item $parkedLocal $local }
    Exit-GameSlot $slot
}
