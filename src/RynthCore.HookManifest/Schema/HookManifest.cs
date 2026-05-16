using System;
using System.Collections.Generic;

namespace RynthCore.HookManifest.Schema;

public sealed record ManifestInput
{
    public required string Path { get; init; }
    public required long FileSizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required int ImageBase { get; init; }
    public required int ImageSizeRva { get; init; }
    public required int TextRva { get; init; }
    public required string Machine { get; init; }
}

public sealed record HookManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string Generator { get; init; }
    public required DateTime GeneratedAtUtc { get; init; }
    public required ManifestInput Input { get; init; }
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyDictionary<string, HookManifestEntry> Symbols { get; init; } =
        new Dictionary<string, HookManifestEntry>();
}
