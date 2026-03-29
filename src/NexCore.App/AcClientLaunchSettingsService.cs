using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NexCore.App;

internal sealed class AcClientLaunchSettingsService
{
    private const string UserPreferencesFileName = "UserPreferences.ini";
    private const string NetSectionName = "Net";
    private const string ComputeUniquePortKey = "ComputeUniquePort";
    private const string IntroVideoFileName = "turbine_logo_ac.avi";
    private const string DisabledIntroVideoSuffix = ".nexcore-disabled";

    public void Apply(string acClientPath, bool allowMultipleClients, bool skipIntroVideos, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(acClientPath) || !File.Exists(acClientPath))
            return;

        string gameDirectory = Path.GetDirectoryName(acClientPath) ?? string.Empty;
        UpdateUserPreferences(allowMultipleClients, log);
        UpdateIntroVideoState(gameDirectory, skipIntroVideos, log);
    }

    private static void UpdateUserPreferences(bool allowMultipleClients, Action<string>? log)
    {
        string documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string prefsDirectory = Path.Combine(documentsDirectory, "Asheron's Call");
        string prefsPath = Path.Combine(prefsDirectory, UserPreferencesFileName);

        Directory.CreateDirectory(prefsDirectory);

        List<string> lines = File.Exists(prefsPath)
            ? File.ReadAllLines(prefsPath).ToList()
            : [];

        bool changed = SetIniValue(lines, NetSectionName, ComputeUniquePortKey, allowMultipleClients ? "True" : "False");
        if (!changed)
            return;

        File.WriteAllLines(prefsPath, lines);
        log?.Invoke($"Launch setting applied: ComputeUniquePort={(allowMultipleClients ? "True" : "False")} in UserPreferences.ini.");
    }

    private static void UpdateIntroVideoState(string gameDirectory, bool skipIntroVideos, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
            return;

        string introVideoPath = Path.Combine(gameDirectory, IntroVideoFileName);
        string parkedIntroVideoPath = introVideoPath + DisabledIntroVideoSuffix;

        if (skipIntroVideos)
        {
            if (File.Exists(parkedIntroVideoPath))
                return;

            if (!File.Exists(introVideoPath))
                return;

            File.Move(introVideoPath, parkedIntroVideoPath);
            log?.Invoke("Launch setting applied: intro video parked so AC opens without the turbine logo movie.");
            return;
        }

        if (!File.Exists(parkedIntroVideoPath) || File.Exists(introVideoPath))
            return;

        File.Move(parkedIntroVideoPath, introVideoPath);
        log?.Invoke("Launch setting applied: intro video restored.");
    }

    private static bool SetIniValue(List<string> lines, string sectionName, string key, string value)
    {
        string sectionHeader = $"[{sectionName}]";
        int sectionIndex = lines.FindIndex(line => string.Equals(line.Trim(), sectionHeader, StringComparison.OrdinalIgnoreCase));

        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);

            lines.Add(sectionHeader);
            lines.Add($"{key}={value}");
            return true;
        }

        int nextSectionIndex = sectionIndex + 1;
        while (nextSectionIndex < lines.Count && !IsSectionHeader(lines[nextSectionIndex]))
            nextSectionIndex++;

        for (int i = sectionIndex + 1; i < nextSectionIndex; i++)
        {
            string line = lines[i].Trim();
            if (!line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                continue;

            string updatedLine = $"{key}={value}";
            if (string.Equals(lines[i], updatedLine, StringComparison.Ordinal))
                return false;

            lines[i] = updatedLine;
            return true;
        }

        lines.Insert(nextSectionIndex, $"{key}={value}");
        return true;
    }

    private static bool IsSectionHeader(string line)
    {
        string trimmed = line.Trim();
        return trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal);
    }
}
