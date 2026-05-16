using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RynthCore.HookManifest.Schema;
using Manifest = RynthCore.HookManifest.Schema.HookManifest;

namespace RynthCore.HookResolver.Core.Resolution;

public static class ManifestBuilder
{
    public const string GeneratorName = "RynthCore.HookResolver 0.1.0";

    public static Manifest Compose(
        ManifestInput input,
        IEnumerable<HookManifestEntry> entries,
        IReadOnlyDictionary<string, string>? labels = null)
    {
        var dict = new Dictionary<string, HookManifestEntry>();
        foreach (var e in entries)
            dict[e.SymbolId] = e;

        return new Manifest
        {
            Generator = GeneratorName,
            GeneratedAtUtc = DateTime.UtcNow,
            Input = input,
            Labels = labels ?? new Dictionary<string, string>(),
            Symbols = dict
        };
    }

    public static async Task WriteAsync(Manifest manifest, string path, CancellationToken ct = default)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest, HookManifestJsonContext.Default.HookManifest);

        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }
}
