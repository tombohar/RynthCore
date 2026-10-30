// ============================================================================
//  RynthCore.Engine - UI/Data/ItemHudStore.cs
//  item_huds.txt for the Item Count and Mini Remote panels, per character
//  ("<server>/<character>", the key the video patch uses). The format and the
//  edits are ItemHudModel.cs (pure); this file holds the loaded contents and
//  does the file I/O on UiBackgroundWriter, never on AC's render thread.
//
//  Threads: the faces read and edit from AC's render thread. Reads are a
//  volatile snapshot for the current character (a lock only when the character
//  changes); edits take the lock briefly and queue the save.
// ============================================================================

using System;
using System.IO;
using System.Threading;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.UI.Data;

internal static class ItemHudStore
{
    private static readonly object Sync = new();
    private static ItemHudFile.Contents? _file;        // under Sync
    private static volatile bool _loaded;
    private static bool _loadQueued;

    private sealed record Snap(string Key, ItemHudCharacter Data);
    private static Snap? _snap;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "item_huds.txt");

    public static bool Loaded => _loaded;

    /// <summary>A character is logged in (its name is known): lists are kept per character.</summary>
    public static bool HasCharacter => SessionStateRegistry.LastCharacterName.Length > 0;

    public static string CharacterKey =>
        D3D9.VideoPatchPolicy.CharacterKey(SessionStateRegistry.LastServerName, SessionStateRegistry.LastCharacterName);

    /// <summary>Queues the one-time read off the calling thread. Any thread; cheap after the first call.</summary>
    public static void EnsureLoadQueued()
    {
        if (_loaded) return;
        lock (Sync)
        {
            if (_loadQueued) return;
            _loadQueued = true;
        }
        UiBackgroundWriter.Enqueue("item huds (load)", LoadNow);
    }

    private static void LoadNow()
    {
        ItemHudFile.Contents contents;
        try
        {
            string path = FilePath;
            contents = File.Exists(path) ? ItemHudFile.Parse(File.ReadAllLines(path)) : new ItemHudFile.Contents();
        }
        catch (Exception ex)
        {
            RynthLog.UI($"ItemHud: reading item_huds.txt failed - {ex.GetType().Name}: {ex.Message}");
            contents = new ItemHudFile.Contents();
        }
        lock (Sync)
        {
            _file = contents;
            Volatile.Write(ref _snap, null);
            _loaded = true;
        }
    }

    /// <summary>The current character's lists (empty before the file is read). Any thread.</summary>
    public static ItemHudCharacter Current
    {
        get
        {
            if (!_loaded) return ItemHudCharacter.Empty;
            string key = CharacterKey;
            Snap? s = Volatile.Read(ref _snap);
            if (s != null && string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) return s.Data;
            lock (Sync)
            {
                ItemHudCharacter d = _file?.For(key) ?? ItemHudCharacter.Empty;
                Volatile.Write(ref _snap, new Snap(key, d));
                return d;
            }
        }
    }

    /// <summary>
    /// Applies an edit to the current character's lists and saves them. False (with the
    /// reason) before the file is read or before a character is logged in.
    /// </summary>
    public static bool Update(Func<ItemHudCharacter, ItemHudCharacter> edit, out string why)
    {
        if (!_loaded) { why = "Still reading the saved lists..."; return false; }
        if (!HasCharacter) { why = "Log in a character first: the lists are kept per character."; return false; }
        string key = CharacterKey;
        string text;
        lock (Sync)
        {
            _file ??= new ItemHudFile.Contents();
            ItemHudCharacter cur = _file.For(key);
            ItemHudCharacter next = edit(cur);
            if (ReferenceEquals(next, cur)) { why = string.Empty; return true; }
            _file.Set(key, next);
            Volatile.Write(ref _snap, new Snap(key, next));
            text = ItemHudFile.Write(_file);
        }
        why = string.Empty;
        UiBackgroundWriter.Enqueue("item huds", () => SaveNow(text));
        return true;
    }

    private static void SaveNow(string text)
    {
        try
        {
            string path = FilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text);
        }
        catch (Exception ex)
        {
            RynthLog.UI($"ItemHud: writing item_huds.txt failed - {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>
/// The character's carried items (packs and worn) as <see cref="HudItem"/>s, rebuilt only when
/// InventoryModel publishes a new view (at most a few times a second). One per face; AC's render thread.
/// </summary>
internal sealed class HudItemCache
{
    private InventoryView? _view;
    private HudItem[] _items = Array.Empty<HudItem>();

    /// <summary>Changes whenever the list is rebuilt.</summary>
    public int Version { get; private set; }

    public HudItem[] Get(InventoryView view)
    {
        if (ReferenceEquals(view, _view)) return _items;
        _view = view;
        int n = view.Worn.Length;
        foreach (InventoryPack p in view.Packs) n += p.Items.Length;
        var list = new HudItem[n];
        int i = 0;
        foreach (InventoryPack p in view.Packs)
            foreach (InventoryItem it in p.Items) list[i++] = new HudItem(it.Id, it.Name, it.Stack);
        foreach (InventoryItem it in view.Worn) list[i++] = new HudItem(it.Id, it.Name, it.Stack);
        _items = list;
        Version++;
        return _items;
    }

    /// <summary>The id of a carried item with this name (for its icon), 0 when none.</summary>
    public uint IdOf(string name)
    {
        foreach (HudItem it in _items)
            if (string.Equals(it.Name, name, StringComparison.OrdinalIgnoreCase)) return it.Id;
        return 0;
    }
}
