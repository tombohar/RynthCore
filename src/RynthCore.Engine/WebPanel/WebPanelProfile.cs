using System.IO;
using System;
using System.Security.Cryptography;
using System.Text;

namespace RynthCore.Engine.WebPanel;

/// <summary>Pure per-world, per-profile-key WebView2 user data folder naming. No file access.</summary>
internal static class WebPanelProfile
{
    /// <summary>
    /// &lt;localAppData&gt;\RynthCore\WebPanel\&lt;world&gt;\&lt;hex&gt;. World: [A-Za-z0-9 _-] kept, others
    /// '_', trimmed, max 64, empty -> "_default". Hex: first 16 bytes of
    /// SHA-256(UTF-8(key.Trim().ToLowerInvariant())) as lowercase hex; empty key -> "default".
    /// </summary>
    public static string Folder(string localAppData, string? worldName, string? profileKey)
    {
        return Path.Combine(localAppData, "RynthCore", "WebPanel", SanitizeWorld(worldName), KeyHex(profileKey));
    }

    private static string SanitizeWorld(string? world)
    {
        if (string.IsNullOrWhiteSpace(world)) return "_default";
        var sb = new StringBuilder();
        foreach (char c in world.Trim())
        {
            bool keep = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                        || c == ' ' || c == '_' || c == '-';
            sb.Append(keep ? c : '_');
        }
        string s = sb.ToString();
        if (s.Length > 64) s = s.Substring(0, 64);
        s = s.Trim(); // a trailing space is not a valid Windows folder name
        return s.Length == 0 ? "_default" : s;
    }

    private static string KeyHex(string? key)
    {
        string k = (key ?? "").Trim().ToLowerInvariant();
        if (k.Length == 0) return "default";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(k));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }
}
