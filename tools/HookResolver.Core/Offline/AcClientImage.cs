using RynthCore.HookManifest.Scanning;
using RynthCore.HookManifest.Schema;

namespace RynthCore.HookResolver.Core.Offline;

public sealed record AcClientImage(AcClientImageView View, ManifestInput Input);
