using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using RynthCore.App;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Per-account dialog for editing OnLogin chat commands. Scoped to a single
/// LaunchAccountProfile so we never accidentally show characters from other
/// accounts. The character list is sourced strictly from:
///   1. The profile's currently-selected CharacterName
///   2. Characters that already have configured commands on this profile
///   3. Names the user adds manually via the "Add character" field
///
/// We pull from CharacterCacheStore.ReadStrict (server-scoped, no fallback).
/// The engine writes the full slot roster from AC's 0xF658 server packet on
/// every char-select visit, scoped per (account, server). The strict reader
/// avoids the legacy account-only fallback file which can leak chars from
/// other accounts/servers populated before per-server scoping existed.
/// </summary>
internal partial class LoginCommandsDialog : Window
{
    private readonly LaunchAccountProfile _profile;
    private readonly LaunchServerProfile? _server;

    private readonly ObservableCollection<string> _characters = new();
    private string? _current;
    private bool _suppressDirtyTracking;
    private bool _isDirty;

    public bool ChangesSaved { get; private set; }

    public LoginCommandsDialog(LaunchAccountProfile profile, LaunchServerProfile? server)
    {
        _profile = profile;
        _server = server;
        InitializeComponent();

        ScopeText.Text = server == null
            ? $"Account: {_profile.AccountName}"
            : $"{_profile.AccountName} @ {server.Name}";

        SeedCharacterList();

        CharacterList.ItemsSource = _characters;
        CharacterList.SelectionChanged += OnCharacterSelectionChanged;

        WaitMsBox.ValueChanged += (_, _) => MarkDirty();
        CommandsBox.TextChanged += (_, _) => MarkDirty();

        SaveButton.Click += (_, _) => SaveCurrent();
        CloseButton.Click += (_, _) => Close();
        AddCharacterButton.Click += (_, _) => AddCharacterFromBox();
        RemoveCharacterButton.Click += (_, _) => RemoveSelectedCharacter();
        RefreshCharactersButton.Click += (_, _) => RefreshFromCache();
        NewCharacterBox.KeyDown += (_, e) =>
        {
            if (e.Key == global::Avalonia.Input.Key.Enter)
                AddCharacterFromBox();
        };

        if (_characters.Count == 0)
        {
            HeaderText.Text = "No characters yet.";
            SubHeaderText.Text = "Add a character on the left to start editing commands.";
            SetEditorEnabled(false);
        }
        else
        {
            CharacterList.SelectedIndex = 0;
        }
    }

    private void SeedCharacterList()
    {
        var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(_profile.CharacterName)
            && _profile.CharacterName != LaunchAccountProfile.NoneOption)
        {
            seen.Add(_profile.CharacterName);
        }

        foreach (string configured in _profile.OnLoginCommandsByCharacter.Keys)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                seen.Add(configured);
        }

        // Strict (account, server) read — avoids the legacy account-only
        // fallback file that could leak characters from prior contexts.
        if (_server != null)
        {
            foreach (string cached in CharacterCacheStore.ReadStrict(_profile.AccountName, _server.Name))
            {
                if (!string.IsNullOrWhiteSpace(cached))
                    seen.Add(cached);
            }
        }

        foreach (string c in seen)
            _characters.Add(c);
    }

    private void RefreshFromCache()
    {
        if (_server == null)
        {
            StatusText.Text = "No server set on this profile — nothing to refresh.";
            return;
        }

        var fresh = CharacterCacheStore.ReadStrict(_profile.AccountName, _server.Name);
        if (fresh.Count == 0)
        {
            StatusText.Text = "Cache is empty for this account+server. Visit char-select once to populate.";
            return;
        }

        // Preserve any user-added characters and any with configured commands;
        // merge the cache list on top.
        var merged = new SortedSet<string>(_characters, StringComparer.OrdinalIgnoreCase);
        foreach (string c in fresh)
            merged.Add(c);

        // Re-seed the visible list from the merged set.
        string? previouslySelected = _current;
        _characters.Clear();
        foreach (string c in merged)
            _characters.Add(c);

        if (previouslySelected != null)
            SelectCharacter(previouslySelected);

        StatusText.Text = $"Loaded {fresh.Count} character(s) from cache.";
    }

    private void OnCharacterSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isDirty && _current != null)
        {
            // Auto-save in-flight edits so a stray click doesn't lose work.
            SaveCurrent();
        }

        _current = CharacterList.SelectedItem as string;
        LoadCurrent();
    }

    private void LoadCurrent()
    {
        _suppressDirtyTracking = true;
        try
        {
            if (_current == null)
            {
                HeaderText.Text = _characters.Count == 0
                    ? "No characters yet."
                    : "Select a character on the left.";
                SubHeaderText.Text = "";
                CommandsBox.Text = "";
                WaitMsBox.Value = _profile.OnLoginWaitMs;
                SetEditorEnabled(false);
                return;
            }

            HeaderText.Text = _current;
            SubHeaderText.Text = _server == null
                ? _profile.AccountName
                : $"{_profile.AccountName} @ {_server.Name}";

            WaitMsBox.Value = _profile.OnLoginWaitMs;
            CommandsBox.Text = _profile.OnLoginCommandsByCharacter.TryGetValue(_current, out List<string>? cmds) && cmds != null
                ? string.Join(Environment.NewLine, cmds)
                : string.Empty;

            SetEditorEnabled(true);
            StatusText.Text = "";
            _isDirty = false;
        }
        finally
        {
            _suppressDirtyTracking = false;
        }
    }

    private void MarkDirty()
    {
        if (_suppressDirtyTracking)
            return;
        _isDirty = true;
        StatusText.Text = "Unsaved changes";
    }

    private void SaveCurrent()
    {
        if (_current == null)
            return;

        var lines = (CommandsBox.Text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (lines.Count == 0)
            _profile.OnLoginCommandsByCharacter.Remove(_current);
        else
            _profile.OnLoginCommandsByCharacter[_current] = lines;

        if (WaitMsBox.Value is decimal w)
            _profile.OnLoginWaitMs = (int)w;

        ChangesSaved = true;
        _isDirty = false;
        StatusText.Text = $"Saved {lines.Count} command(s) for {_current}.";
    }

    private void AddCharacterFromBox()
    {
        string name = (NewCharacterBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
            return;

        if (!_characters.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
        {
            _characters.Add(name);
            // Resort with case-insensitive ordinal compare for stable display.
            ResortCharacters();
        }

        NewCharacterBox.Text = string.Empty;
        SelectCharacter(name);
        SetEditorEnabled(true);
    }

    private void RemoveSelectedCharacter()
    {
        if (_current == null)
            return;

        // Removing also clears any saved commands for that character so the
        // profile state matches the visible list.
        _profile.OnLoginCommandsByCharacter.Remove(_current);
        _characters.Remove(_current);
        ChangesSaved = true;
        _current = null;

        if (_characters.Count > 0)
            CharacterList.SelectedIndex = 0;
        else
            LoadCurrent();
    }

    private void SelectCharacter(string name)
    {
        for (int i = 0; i < _characters.Count; i++)
        {
            if (string.Equals(_characters[i], name, StringComparison.OrdinalIgnoreCase))
            {
                CharacterList.SelectedIndex = i;
                return;
            }
        }
    }

    private void ResortCharacters()
    {
        var sorted = _characters.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        _characters.Clear();
        foreach (string c in sorted)
            _characters.Add(c);
    }

    private void SetEditorEnabled(bool enabled)
    {
        CommandsBox.IsEnabled = enabled;
        WaitMsBox.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        RemoveCharacterButton.IsEnabled = enabled;
    }
}
