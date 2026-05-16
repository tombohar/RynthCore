namespace RynthCore.HookManifest.Scanning;

public sealed class AcClientImageView
{
    public AcClientImageView(
        int imageBase, int imageSize,
        int textRva, byte[] textBytes,
        int rdataRva = 0, byte[]? rdataBytes = null)
    {
        ImageBase = imageBase;
        ImageSize = imageSize;
        TextRva = textRva;
        TextBytes = textBytes;
        RdataRva = rdataRva;
        RdataBytes = rdataBytes ?? [];
    }

    public int ImageBase { get; }
    public int ImageSize { get; }
    public int TextRva { get; }
    public int TextBaseVa => ImageBase + TextRva;
    public byte[] TextBytes { get; }

    public int RdataRva { get; }
    public int RdataBaseVa => ImageBase + RdataRva;
    public byte[] RdataBytes { get; }
    public bool HasRdata => RdataBytes.Length > 0;
}
