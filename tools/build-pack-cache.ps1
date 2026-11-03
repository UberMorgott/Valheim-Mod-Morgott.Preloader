# Copyright (c) 2026 Morgott. Licensed under CC BY-NC 4.0 (see LICENSE).
#
# Builds the modpack's prebuilt Morgott.Preloader bundle cache from a warm local cache.
#
#   pwsh -File tools\build-pack-cache.ps1 -PluginDirs Z:\modpacks\Valheim\universal\BepInEx\plugins,Z:\modpacks\Valheim\client\BepInEx\plugins -OutDir <dir>
#
# Input: the local cache Morgott.Preloader filled on this PC (<game>\BepInEx\Morgott.Preloader\cache\bundles\v1-<Unity>\<mvid>-<name hash>.bundle,
# made after a normal launch reached the main menu and the background recompress finished) and the pack's plugin DLLs.
# Output: <OutDir>\v1-<Unity>\ with one copy per bundle whose source DLL (by MVID) is in the pack, plus manifest.tsv. Copies of DLLs
# not in the pack are left out (Morgott.Preloader would never use them). Same inputs = byte-identical output (sorted, no times or paths).
# The pack ships <OutDir>'s content as client\BepInEx\Morgott.Preloader\pack\bundles\ (the launcher puts it in the game folder):
#   <game>\BepInEx\Morgott.Preloader\pack\bundles\v1-6000.0.75f1\manifest.tsv + *.bundle
# Morgott.Preloader only reads that folder; each copy is used only when its manifest row matches the loaded DLL's resource (README).
param(
    [string]$CacheDir = 'D:\Steam\steamapps\common\Valheim\BepInEx\Morgott.Preloader\cache\bundles',
    [Parameter(Mandatory)][string[]]$PluginDirs,
    [Parameter(Mandatory)][string]$OutDir
)
$ErrorActionPreference = 'Stop'
$PluginDirs = @($PluginDirs -split ',' | ForEach-Object Trim | Where-Object { $_ })   # `pwsh -File` passes 'a,b' as one string

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

public static class Morgott.PreloaderPackCache
{
    // Same key as Morgott.Preloader's ResourceSource.FileName: "<mvid N>-<fnv1a64(resource name UTF-16 chars) x16>.bundle".
    public static string Key(Guid mvid, string resourceName)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in resourceName)
        {
            hash = unchecked((hash ^ c) * 1099511628211UL);
        }
        return mvid.ToString("N") + "-" + hash.ToString("x16") + ".bundle";
    }

    // Embedded UnityFS resources of a .NET DLL: key -> (resource name, bytes). Empty for native or resource-less DLLs.
    public static Dictionary<string, KeyValuePair<string, byte[]>> Bundles(string dll)
    {
        var result = new Dictionary<string, KeyValuePair<string, byte[]>>(StringComparer.OrdinalIgnoreCase);
        using (var pe = new PEReader(File.OpenRead(dll)))
        {
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader == null)
            {
                return result;
            }
            MetadataReader md = pe.GetMetadataReader();
            Guid mvid = md.GetGuid(md.GetModuleDefinition().Mvid);
            int resources = pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress;
            foreach (ManifestResourceHandle handle in md.ManifestResources)
            {
                ManifestResource resource = md.GetManifestResource(handle);
                if (!resource.Implementation.IsNil)
                {
                    continue; // lives in another file
                }
                BlobReader reader = pe.GetSectionData(resources + (int)resource.Offset).GetReader();
                byte[] bytes = reader.ReadBytes(reader.ReadInt32());
                if (bytes.Length > 8 && Encoding.ASCII.GetString(bytes, 0, 8) == "UnityFS\0")
                {
                    string name = md.GetString(resource.Name);
                    result[Key(mvid, name)] = new KeyValuePair<string, byte[]>(name, bytes);
                }
            }
        }
        return result;
    }

    // UnityFS signature and the header's total size (big-endian i64 after two C strings) equal to the file length.
    public static bool IsComplete(string path)
    {
        using (var file = File.OpenRead(path))
        {
            var header = new byte[Math.Min(1024, (int)Math.Min(file.Length, 1024))];
            file.Read(header, 0, header.Length);
            if (header.Length < 20 || Encoding.ASCII.GetString(header, 0, 8) != "UnityFS\0")
            {
                return false;
            }
            int at = 12;
            for (int s = 0; s < 2; s++)
            {
                at = Array.IndexOf(header, (byte)0, at) + 1;
                if (at <= 0 || at + 8 > header.Length)
                {
                    return false;
                }
            }
            long size = 0;
            for (int i = 0; i < 8; i++)
            {
                size = (size << 8) | header[at + i];
            }
            return size == file.Length;
        }
    }
}
'@

function Get-Sha256([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }

$versions = @(Get-ChildItem -LiteralPath $CacheDir -Directory -Filter 'v1-*')
if ($versions.Count -ne 1) { throw "expected exactly one v1-<Unity version> folder in $CacheDir, found $($versions.Count)" }
$version = $versions[0]

# Every embedded UnityFS resource of every pack DLL, by cache key.
$sources = @{}
foreach ($dll in $PluginDirs | ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -Filter *.dll } | Sort-Object FullName) {
    try { $bundles = [Morgott.PreloaderPackCache]::Bundles($dll.FullName) }
    catch [BadImageFormatException] { continue }
    foreach ($kv in $bundles.GetEnumerator()) {
        $source = [pscustomobject]@{ Dll = $dll.Name; Name = $kv.Value.Key; Length = $kv.Value.Value.Length; Sha = Get-Sha256 $kv.Value.Value }
        $seen = $sources[$kv.Key]
        if ($seen -and $seen.Sha -ne $source.Sha) { throw "key $($kv.Key): $($seen.Dll) and $($dll.FullName) share an MVID but differ" }
        if (-not $seen) { $sources[$kv.Key] = $source }   # the same DLL twice (e.g. universal + client): first path wins
    }
}

# Only a previous build (or nothing) may be replaced: never a cache or plugin folder given by mistake.
$OutDir = [IO.Path]::GetFullPath($OutDir).TrimEnd('\')
foreach ($in in @($CacheDir) + $PluginDirs) {
    $full = [IO.Path]::GetFullPath($in).TrimEnd('\')
    if ("$full\".StartsWith("$OutDir\", [StringComparison]::OrdinalIgnoreCase) -or "$OutDir\".StartsWith("$full\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "-OutDir $OutDir overlaps input $full"
    }
}
if ((Test-Path -LiteralPath $OutDir) -and (Get-ChildItem -LiteralPath $OutDir -Recurse -File | Where-Object { $_.Name -ne 'manifest.tsv' -and $_.Extension -ne '.bundle' })) {
    throw "-OutDir $OutDir holds files other than a pack cache build"
}
$stage = "$OutDir.building"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$target = New-Item -ItemType Directory -Force (Join-Path $stage $version.Name)
$rows = [Collections.Generic.List[string]]::new()
$notInPack = 0
$bytes = 0L
foreach ($copy in Get-ChildItem -LiteralPath $version.FullName -File -Filter *.bundle | Sort-Object Name) {
    $source = $sources[$copy.Name]
    if (-not $source) { $notInPack++; continue }
    if (-not [Morgott.PreloaderPackCache]::IsComplete($copy.FullName)) { throw "$($copy.FullName) is not a complete UnityFS file (recompress still running?)" }
    $staged = Copy-Item -LiteralPath $copy.FullName -Destination $target.FullName -PassThru   # the manifest describes the staged bytes
    $copySha = (Get-FileHash -LiteralPath $staged.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $rows.Add(($copy.Name, $source.Name, $source.Length, $source.Sha, $staged.Length, $copySha, $source.Dll) -join "`t")
    $bytes += $staged.Length
}
if ($rows.Count -eq 0) { throw "no cached copy in $($version.FullName) belongs to a DLL under $($PluginDirs -join ', ')" }
$sorted = $rows.ToArray(); [Array]::Sort($sorted, [StringComparer]::Ordinal)   # rows start with the unique file name
$header = "# Morgott.Preloader pack bundle cache $($version.Name): file`tresource`tsource bytes`tsource sha256`tcopy bytes`tcopy sha256`towner dll"
[IO.File]::WriteAllText((Join-Path $target.FullName 'manifest.tsv'), (@($header) + $sorted | ForEach-Object { "$_`n" }) -join '', [Text.UTF8Encoding]::new($false))

# Publish: replace the previous build only once the new one is complete.
$previous = "$OutDir.previous"
if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
if (Test-Path -LiteralPath $OutDir) { Move-Item -LiteralPath $OutDir -Destination $previous }
try { Move-Item -LiteralPath $stage -Destination $OutDir }
catch { if (Test-Path -LiteralPath $previous) { Move-Item -LiteralPath $previous -Destination $OutDir }; throw }
if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }

$missing = @($sources.Keys | Where-Object { -not (Test-Path -LiteralPath (Join-Path $version.FullName $_)) } | Sort-Object)
"PACK CACHE $OutDir\$($version.Name): $($rows.Count) copies, $([math]::Round($bytes / 1MB)) MB; $notInPack local copies skipped (DLL not in the pack)"
if ($missing.Count) {
    "$($missing.Count) embedded UnityFS resources in the pack have no local copy (already LZ4, not loaded through LoadFromStream, or not warmed yet):"
    $missing | ForEach-Object { "  $_  $($sources[$_].Dll):$($sources[$_].Name)" }
}
