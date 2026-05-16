namespace RynthCore.HookManifest.Schema;

public sealed record ResolvedAddress(int Va, int Rva)
{
    public string Hex => $"0x{Va:X8}";
}
