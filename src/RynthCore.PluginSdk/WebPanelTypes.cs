using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RynthCore.PluginSdk;

// v94: a WebView2 web panel over the game window. The native structs mirror
// RynthCore.Engine/Plugins/PluginContract.cs field for field (tools\PluginManifestTests checks sizes and
// offsets). The API number is provisional.

/// <summary>v94 WebPanelOpen argument (Pack 4; 48 bytes on x86). Mirrors the engine's WebPanelOpenNative.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct WebPanelOpenNative
{
    /// <summary>sizeof this struct; the engine accepts Size &gt;= OffsetOf(Reserved) (32 on x86).</summary>
    public uint Size;
    /// <summary><see cref="WebPanelFlags"/>; unknown bits ignored.</summary>
    public uint Flags;
    /// <summary>UTF-8 NUL-terminated, at most 4096 bytes.</summary>
    public byte* Url;
    /// <summary>UTF-8, '\n'-separated allowlist.</summary>
    public byte* Allowlist;
    /// <summary>UTF-8, may be null; at most 256 bytes.</summary>
    public byte* ProfileKey;
    /// <summary>UTF-8, may be null; at most 128 bytes.</summary>
    public byte* Title;
    /// <summary>0 = 88, else 30..100 (only when no remembered layout).</summary>
    public int WidthPercent;
    public int HeightPercent;
    public fixed uint Reserved[4];
}

/// <summary>v94 WebPanelGetState result (Pack 4; 48 bytes). Mirrors the engine's WebPanelStateNative.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct WebPanelStateNative
{
    public uint Size;
    public int Handle;
    /// <summary><see cref="WebPanelStatus"/>.</summary>
    public int Status;
    /// <summary><see cref="WebPanelStateFlags"/>.</summary>
    public uint Flags;
    /// <summary>0 or a negative <see cref="WebPanelResult"/> (RuntimeMissing / CreateFailed / ProcessFailed).</summary>
    public int Error;
    public int LastHttpStatus;
    public uint NavigationCount;
    public fixed uint Reserved[5];
}

/// <summary>Options for <see cref="RynthCoreHost.OpenWebPanel"/>.</summary>
[Flags]
public enum WebPanelFlags : uint
{
    None = 0,
    /// <summary>When the panel cannot open (no WebView2 runtime, creation failed), open the URL in the system browser.</summary>
    FallbackToBrowser = 1,
    /// <summary>Escape goes to the page instead of closing the panel.</summary>
    PageKeepsEscape = 2,
    // 4 is reserved: phase 2 EnableMessages.
}

/// <summary>Results of the web panel calls; an open that succeeds returns a handle &gt; 0 instead.</summary>
public enum WebPanelResult : int
{
    Queued = 1, AlreadyClosed = 0, BadArgs = -1, UrlNotAllowed = -2, NoGameWindow = -3,
    Unavailable = -4, ShuttingDown = -5, StaleHandle = -6, RuntimeMissing = -7, CreateFailed = -8, ProcessFailed = -9,
}

/// <summary>Where the web panel is in its life.</summary>
public enum WebPanelStatus : int
{
    None = 0, Starting = 1, Loading = 2, Ready = 3, Closed = 4, Failed = 5, Superseded = 6,
}

[Flags]
public enum WebPanelStateFlags : uint
{
    None = 0, Visible = 1, Focused = 2, OpenedInBrowser = 4,
}

/// <summary>A snapshot of the web panel from <see cref="RynthCoreHost.GetWebPanelState"/>.</summary>
public readonly record struct WebPanelState(int Handle, WebPanelStatus Status, WebPanelStateFlags Flags, WebPanelResult Error, int LastHttpStatus, uint NavigationCount);

public sealed class WebPanelOptions
{
    public required string Url { get; init; }
    /// <summary>1..16 entries, no '\n' inside an entry.</summary>
    public required IReadOnlyList<string> Allowlist { get; init; }
    /// <summary>Opaque; the engine lower-cases, hashes and adds the world.</summary>
    public string ProfileKey { get; init; } = "";
    public string Title { get; init; } = "";
    public int WidthPercent { get; init; } = 0;
    public int HeightPercent { get; init; } = 0;
    public WebPanelFlags Flags { get; init; } = WebPanelFlags.FallbackToBrowser;
}
