using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RynthCore.HookManifest.Schema;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HookManifest))]
[JsonSerializable(typeof(HookManifestEntry))]
[JsonSerializable(typeof(ResolvedAddress))]
[JsonSerializable(typeof(ValidationRecord))]
[JsonSerializable(typeof(ManifestInput))]
[JsonSerializable(typeof(FunctionSnapshot))]
[JsonSerializable(typeof(CallTarget))]
[JsonSerializable(typeof(Dictionary<string, HookManifestEntry>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class HookManifestJsonContext : JsonSerializerContext;
