using System;
using System.Text;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.WebPanel;

/// <summary>One open request, every string already copied out of the caller's buffers.</summary>
internal sealed record WebPanelRequest(string Url, string[] Allowlist, string ProfileFolder, string Title, int WidthPercent, int HeightPercent, uint Flags);

/// <summary>
/// Reads the v94 WebPanelOpen argument. The SDK passes pointers into stack or pooled buffers that are gone
/// the moment the call returns, so every string is copied here, synchronously, before anything is queued.
/// Pure: no Win32, no WebView2.
/// </summary>
internal static unsafe class WebPanelNativeArgs
{
    public const int MaxAllowlistBytes = 8192, MaxProfileKeyBytes = 256, MaxTitleBytes = 128;
    public const uint KnownFlags = 1 | 2; // FallbackToBrowser, PageKeepsEscape

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>0 and a request, or BadArgs. URL policy (IsAllowed) is the host's to apply.</summary>
    public static int TryBuildRequest(WebPanelOpenNative* open, string localAppData, string? worldName, out WebPanelRequest? request)
    {
        request = null;
        if (open == null) return (int)WebPanelCode.BadArgs;
        uint minSize = (uint)((byte*)open->Reserved - (byte*)open);
        if (open->Size < minSize) return (int)WebPanelCode.BadArgs;
        if (!TryReadUtf8(open->Url, WebPanelUrlPolicy.MaxUrlBytes, true, out string url)) return (int)WebPanelCode.BadArgs;
        if (!TryReadUtf8(open->Allowlist, MaxAllowlistBytes, true, out string allowText)) return (int)WebPanelCode.BadArgs;
        if (!TryReadUtf8(open->ProfileKey, MaxProfileKeyBytes, false, out string key)) return (int)WebPanelCode.BadArgs;
        if (!TryReadUtf8(open->Title, MaxTitleBytes, false, out string title)) return (int)WebPanelCode.BadArgs;
        if (!WebPanelUrlPolicy.TryParseAllowlist(allowText, out string[] allowlist)) return (int)WebPanelCode.BadArgs;
        string folder = WebPanelProfile.Folder(localAppData, worldName, key);
        request = new WebPanelRequest(url, allowlist, folder, title, open->WidthPercent, open->HeightPercent, open->Flags & KnownFlags);
        return 0;
    }

    /// <summary>
    /// A NUL-terminated UTF-8 string of at most <paramref name="maxBytes"/> bytes, copied. Null pointer:
    /// "" (false when required). Required strings must not be empty; invalid UTF-8 is refused.
    /// </summary>
    public static bool TryReadUtf8(byte* p, int maxBytes, bool required, out string value)
    {
        value = "";
        if (p == null) return !required;
        int n = 0;
        while (n <= maxBytes && p[n] != 0) n++;
        if (n > maxBytes) return false;
        if (n == 0) return !required;
        try { value = StrictUtf8.GetString(p, n); }
        catch (DecoderFallbackException) { return false; }
        return true;
    }
}
