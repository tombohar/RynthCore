#Requires -Version 7
<#
.SYNOPSIS
    Reads RynthCore plugin manifests and the engine's plugin API version, for Publish-Update.ps1.

.DESCRIPTION
    Dot-source it:  . (Join-Path $PSScriptRoot "PluginManifest.ps1")

    Get-RynthPluginManifest <dll>   the manifest embedded in a plugin DLL (RCDATA resource
                                    RYNTH_PLUGIN_MANIFEST, written by the SDK's build targets)
                                    as an object, or $null when the DLL has none. The DLL is
                                    mapped as a data file only (LOAD_LIBRARY_AS_DATAFILE): none
                                    of its code runs.
    Get-RynthEngineApi <repoRoot>   PluginContractVersion.Current from the engine's source.

    Read-only; safe to run on its own (tools\PluginManifestTests\Test-PluginManifestPs.ps1 does).
#>

if (-not ('RynthCore.Publish.PeResource' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace RynthCore.Publish
{
    public static class PeResource
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindResourceW(IntPtr module, string name, IntPtr type);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr res);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr data);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr res);

        private const uint LOAD_LIBRARY_AS_DATAFILE = 0x2;
        private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x20;

        /// <summary>RCDATA resource <paramref name="name"/> of a PE file, or null if it has none.</summary>
        public static byte[] ReadRcData(string path, string name)
        {
            IntPtr module = LoadLibraryExW(path, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
            if (module == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "can't map " + path);
            try
            {
                IntPtr res = FindResourceW(module, name, (IntPtr)10);   // RT_RCDATA
                if (res == IntPtr.Zero) return null;
                uint size = SizeofResource(module, res);
                IntPtr data = LockResource(LoadResource(module, res));
                if (data == IntPtr.Zero) return null;
                byte[] bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, (int)size);
                return bytes;
            }
            finally
            {
                FreeLibrary(module);
            }
        }
    }
}
'@
}

function Get-RynthPluginManifest([Parameter(Mandatory)][string]$Path) {
    $bytes = [RynthCore.Publish.PeResource]::ReadRcData((Resolve-Path $Path).Path, "RYNTH_PLUGIN_MANIFEST")
    if ($null -eq $bytes) { return $null }
    $json = [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF).TrimEnd([char]0)
    $m = $json | ConvertFrom-Json
    if (-not $m.name) { throw "$Path has a plugin manifest without a name" }
    if ($null -ne $m.minEngineApi -and -not ("$($m.minEngineApi)" -match '^\d+$')) { throw "$Path has a plugin manifest with a bad minEngineApi '$($m.minEngineApi)'" }
    return $m
}

function Get-RynthEngineApi([Parameter(Mandatory)][string]$RepoRoot) {
    $file = Join-Path $RepoRoot "src\RynthCore.Engine\Plugins\PluginContractVersion.cs"
    $m = [regex]::Match((Get-Content $file -Raw), 'const\s+uint\s+Current\s*=\s*(\d+)')
    if (-not $m.Success) { throw "No PluginContractVersion.Current in $file" }
    return [int]$m.Groups[1].Value
}
