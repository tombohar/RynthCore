using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// On startup, reads a user-editable file at
/// <c>%APPDATA%\RynthCore\string_anchors.txt</c> and, for each non-blank /
/// non-comment line, runs <see cref="StringRefScanner.DumpStringAnchors"/>.
///
/// Workflow for figuring out which function a string anchors:
///   1. Pick a candidate string you think might live in AC's body for a
///      function you want to hook (e.g. "ClientMagicSystem::CastSpell",
///      "Combat::CastSpell"). One per line in the file.
///   2. Launch any RynthCore-injected acclient.exe.
///   3. Read the resulting Compat lines in <c>RynthCore.log</c>:
///        StringRef: '...' @ 0xVA — pushed by N site(s); function entries: 0x...
///      A single function entry means that string uniquely identifies that
///      function — wire it into the relevant hook as the anchor.
///   4. Iterate the file until each function you care about has a unique
///      string anchor.
///
/// File format: one literal per line, blank lines and lines starting with
/// '#' are ignored. No quoting; everything between BOM-stripped start of
/// line and the trailing newline is the literal.
/// </summary>
internal static class StringAnchorDiagnostic
{
    private static readonly byte[] CombatPrologue =
        [0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8];

    private static readonly byte[] GenericPrologue =
        [0x55, 0x8B, 0xEC]; // push ebp; mov ebp, esp — wider net than CombatPrologue

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RynthCore",
        "string_anchors.txt");

    public static void RunIfConfigured()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path))
                return; // No file → no diagnostic; quiet.

            string[] rawLines = File.ReadAllLines(path);
            var literals = new List<string>(rawLines.Length);
            foreach (string raw in rawLines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith('#')) continue;
                literals.Add(line);
            }

            if (literals.Count == 0)
            {
                RynthLog.Compat($"StringAnchorDiagnostic: '{path}' has no candidate strings (all blank/comment).");
                return;
            }

            if (!AcClientModule.TryReadTextSection(out AcClientTextSection module))
            {
                RynthLog.Compat("StringAnchorDiagnostic: acclient.exe not readable, skipping.");
                return;
            }

            // One-time per-process dump of every ASCII string ≥ 8 chars in
            // the image. Lives next to engine.json so the user (or this
            // engine on next launch) can grep it for real anchor candidates
            // when guessing fails. Cheap (~1s) and read-only.
            string dumpPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RynthCore",
                "acclient_strings.txt");
            try
            {
                StringRefScanner.DumpAllStrings(module, dumpPath, minLength: 8);
            }
            catch (Exception ex)
            {
                RynthLog.Compat($"StringAnchorDiagnostic: string dump failed — {ex.GetType().Name}: {ex.Message}");
            }

            RynthLog.Compat($"StringAnchorDiagnostic: scanning {literals.Count} candidate string(s) against acclient.exe ({module.Bytes.Length:X} bytes).");

            // Try the tight CombatPrologue first (matches the same prologue
            // pattern the existing pattern scans use, so anchor results are
            // directly comparable to what _castSpell etc. would bind to).
            RynthLog.Compat("StringAnchorDiagnostic: --- pass 1: CombatPrologue (83 EC 0C 53 56 57 E8) ---");
            StringRefScanner.DumpStringAnchors(module, literals, CombatPrologue);

            // Then a generic push-ebp prologue. Many AC functions don't use
            // the combat prologue — this widens the net.
            RynthLog.Compat("StringAnchorDiagnostic: --- pass 2: generic prologue (55 8B EC push ebp; mov ebp, esp) ---");
            StringRefScanner.DumpStringAnchors(module, literals, GenericPrologue);

            RynthLog.Compat("StringAnchorDiagnostic: scan complete.");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"StringAnchorDiagnostic: failed — {ex.GetType().Name}: {ex.Message}");
        }
    }
}
