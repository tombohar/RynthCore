// ============================================================================
//  RynthCore.Engine - UI/Data/LootProfilePick.cs
//  Settings > Looting's "Loot Profile" row (UI/Data/SettingsData.cs): which
//  entry of RynthAi's loot profile list ("None", then LootProfiles' *.utl and
//  *.json, the dashboard snapshot's lootProfiles) is the profile in use. The
//  pick itself goes through RynthPluginSelectProfile (kind 1), the dashboard
//  pickers' path, so RynthAi saves it and says so in chat.
//  Pure (linked into RynthSuite's RynthAiHostTests).
// ============================================================================

using System;

namespace RynthCore.Engine.UI.Data;

internal static class LootProfilePick
{
    /// <summary>
    /// The entry for <paramref name="currentPath"/>: 0 (None) when no profile is in use; the entry with
    /// its file name when listed; else RynthAi's own <paramref name="selectedIdx"/>, kept in range.
    /// An empty list: 0.
    /// </summary>
    public static int Index(string[]? items, string? currentPath, int selectedIdx)
    {
        if (items == null || items.Length == 0) return 0;
        string path = (currentPath ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0) return 0;
        string name = FileName(path);
        for (int i = 1; i < items.Length; i++)
            if (string.Equals(items[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return Math.Clamp(selectedIdx, 0, items.Length - 1);
    }

    // Path.GetFileName without System.IO (either separator).
    private static string FileName(string path)
    {
        int cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return cut < 0 ? path : path[(cut + 1)..];
    }
}
