using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Runtime tab → Logging card and the live global Logging level box. Every change is written
/// straight to engine.json / diagnostics.json (see <see cref="LoggingSettingsStore"/>); running
/// clients poll those files, so nothing needs a restart or the Save button.
/// </summary>
internal partial class MainWindow
{
    private static readonly IBrush LogNameBrush = new SolidColorBrush(Color.Parse("#EAF0F4"));
    private static readonly IBrush LogMutedBrush = new SolidColorBrush(Color.Parse("#9AA8B3"));

    // Set while the rows are being (re)built so programmatic selections don't write files.
    private bool _suppressLoggingEvents;

    /// <summary>Wires the Logging card and the live global level box. Called once after LoadRuntimeControls.</summary>
    private void InitLoggingControls()
    {
        LoggingLevelComboBox.SelectionChanged += (_, _) => OnGlobalLoggingLevelChanged();
        ReloadLoggingButton.Click += (_, _) => BuildLoggingRows();
        BuildLoggingRows();
    }

    /// <summary>Global level changed: persist to the launcher settings and push to engine.json now.</summary>
    private void OnGlobalLoggingLevelChanged()
    {
        if (_suppressLoggingEvents) return;
        string level = GetSelectedLoggingLevel();
        if (string.Equals(level, _settings.LoggingLevel, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            _settings.LoggingLevel = level;
            SaveSettings();
            EngineJsonStore.SetString("LoggingLevel", level);
            AppendActivity($"Logging level set to {level} (applied live to running clients).");
        }
        catch (Exception ex)
        {
            AppendActivity($"Failed to save logging level: {ex.Message}");
        }
    }

    /// <summary>(Re)builds the engine category, RynthAi category and RynthAi event rows from disk.</summary>
    private void BuildLoggingRows()
    {
        _suppressLoggingEvents = true;
        try
        {
            EngineLogCategoriesPanel.Children.Clear();
            Dictionary<string, string> engineLevels = LoggingSettingsStore.ReadEngineCategoryLevels();
            foreach (LoggingSettingsStore.EngineCategory cat in LoggingSettingsStore.EngineCategories)
            {
                string name = cat.Name;
                EngineLogCategoriesPanel.Children.Add(BuildLoggingRow(
                    name, cat.Description, LoggingSettingsStore.EngineCategoryLevels, engineLevels[name],
                    level =>
                    {
                        LoggingSettingsStore.SetEngineCategoryLevel(name, level);
                        AppendActivity($"Engine log category {name} set to {level} (applied live).");
                    }));
            }

            RynthAiLogCategoriesPanel.Children.Clear();
            RynthAiLogEventsPanel.Children.Clear();
            bool ok = LoggingSettingsStore.TryReadRynthAi(
                out List<LoggingSettingsStore.DiagnosticsRow> categories,
                out List<LoggingSettingsStore.DiagnosticsRow> events,
                out string? error);

            foreach (LoggingSettingsStore.DiagnosticsRow row in categories)
            {
                string key = row.Key;
                RynthAiLogCategoriesPanel.Children.Add(BuildLoggingRow(
                    key, row.Description, LoggingSettingsStore.RynthAiLevels, row.Level,
                    level =>
                    {
                        LoggingSettingsStore.SetRynthAiCategoryLevel(key, level);
                        AppendActivity($"RynthAi log category {key} set to {level} (applied live).");
                    }));
            }
            foreach (LoggingSettingsStore.DiagnosticsRow row in events)
            {
                string key = row.Key;
                RynthAiLogEventsPanel.Children.Add(BuildLoggingRow(
                    key, row.Description, LoggingSettingsStore.RynthAiLevels, row.Level,
                    level =>
                    {
                        LoggingSettingsStore.SetRynthAiEventLevel(key, level);
                        AppendActivity($"RynthAi log event {key} set to {level} (applied live).");
                    }));
            }

            LoggingStatusText.Text = ok && error == null
                ? $"engine.json: {EngineJsonStore.Path}\ndiagnostics.json: {LoggingSettingsStore.DiagnosticsPath}"
                : $"RynthAi: {error}";
        }
        catch (Exception ex)
        {
            LoggingStatusText.Text = $"Failed to load logging settings: {ex.Message}";
        }
        finally
        {
            _suppressLoggingEvents = false;
        }
    }

    /// <summary>One "Name | level box | description" row. <paramref name="apply"/> runs on user changes only.</summary>
    private Control BuildLoggingRow(string name, string description, string[] levels, string current, Action<string> apply)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,110,*") };

        grid.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = LogNameBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var combo = new ComboBox { ItemsSource = levels, MinWidth = 100, Margin = new global::Avalonia.Thickness(0, 0, 8, 0) };
        int index = Array.FindIndex(levels, l => string.Equals(l, current, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = index >= 0 ? index : 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (_suppressLoggingEvents || combo.SelectedItem is not string level) return;
            try { apply(level); }
            catch (Exception ex)
            {
                AppendActivity($"Failed to set {name} to {level}: {ex.Message}");
                // Show what is actually on disk; posted so the combo raising this event isn't torn down mid-handler.
                global::Avalonia.Threading.Dispatcher.UIThread.Post(BuildLoggingRows);
            }
        };
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);

        var desc = new TextBlock
        {
            Text = description,
            Foreground = LogMutedBrush,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(desc, 2);
        grid.Children.Add(desc);
        return grid;
    }
}
