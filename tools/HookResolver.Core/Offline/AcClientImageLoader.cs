using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using RynthCore.HookManifest.Scanning;
using RynthCore.HookManifest.Schema;

namespace RynthCore.HookResolver.Core.Offline;

public static class AcClientImageLoader
{
    private const ushort DosSignature = 0x5A4D;
    private const uint NtSignature = 0x00004550;
    private const int DosPeOffset = 0x3C;
    private const int FileHeaderMachineOffset = 0x0;
    private const int FileHeaderNumSectionsOffset = 0x2;
    private const int FileHeaderOptionalSizeOffset = 0x10;
    private const int OptionalHeaderImageBaseOffset = 0x1C;
    private const int OptionalHeaderSizeOfImageOffset = 0x38;
    private const int SectionHeaderSize = 0x28;
    private const int SectionNameLength = 8;

    public static async Task<AcClientImage> LoadAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("acclient.exe not found.", path);

        byte[] fileBytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (fileBytes.Length < 0x200)
            throw new InvalidDataException("File is too small to be a valid PE.");

        if (BitConverter.ToUInt16(fileBytes, 0) != DosSignature)
            throw new InvalidDataException("Missing MZ DOS signature.");

        int peOffset = BitConverter.ToInt32(fileBytes, DosPeOffset);
        if (peOffset <= 0 || peOffset + 0x18 > fileBytes.Length)
            throw new InvalidDataException("Invalid PE header offset.");

        if (BitConverter.ToUInt32(fileBytes, peOffset) != NtSignature)
            throw new InvalidDataException("Missing PE NT signature.");

        int fileHeaderOffset = peOffset + 4;
        ushort machine = BitConverter.ToUInt16(fileBytes, fileHeaderOffset + FileHeaderMachineOffset);
        ushort numSections = BitConverter.ToUInt16(fileBytes, fileHeaderOffset + FileHeaderNumSectionsOffset);
        ushort optionalHeaderSize = BitConverter.ToUInt16(fileBytes, fileHeaderOffset + FileHeaderOptionalSizeOffset);

        int optionalHeaderOffset = fileHeaderOffset + 0x14;  // FILE_HEADER is 20 bytes
        int imageBase = BitConverter.ToInt32(fileBytes, optionalHeaderOffset + OptionalHeaderImageBaseOffset);
        int imageSize = BitConverter.ToInt32(fileBytes, optionalHeaderOffset + OptionalHeaderSizeOfImageOffset);

        int sectionTableOffset = optionalHeaderOffset + optionalHeaderSize;
        if (!TryFindSection(fileBytes, sectionTableOffset, numSections, ".text",
                out int textRva, out int textRawOffset, out int textRawSize, out int textVirtualSize))
        {
            throw new InvalidDataException(".text section not found.");
        }

        int textBytesLength = Math.Max(textVirtualSize, textRawSize);
        textBytesLength = Math.Min(textBytesLength, fileBytes.Length - textRawOffset);
        if (textBytesLength <= 0)
            throw new InvalidDataException(".text section is empty or truncated.");

        byte[] textBytes = new byte[textBytesLength];
        Array.Copy(fileBytes, textRawOffset, textBytes, 0, Math.Min(textRawSize, textBytesLength));

        int rdataRva = 0;
        byte[] rdataBytes = [];
        if (TryFindSection(fileBytes, sectionTableOffset, numSections, ".rdata",
                out int rRva, out int rRawOffset, out int rRawSize, out int rVirtualSize))
        {
            int rdataLen = Math.Max(rVirtualSize, rRawSize);
            rdataLen = Math.Min(rdataLen, fileBytes.Length - rRawOffset);
            if (rdataLen > 0)
            {
                rdataRva = rRva;
                rdataBytes = new byte[rdataLen];
                Array.Copy(fileBytes, rRawOffset, rdataBytes, 0, Math.Min(rRawSize, rdataLen));
            }
        }

        string sha256 = ComputeSha256(fileBytes);

        var view = new AcClientImageView(imageBase, imageSize, textRva, textBytes, rdataRva, rdataBytes);
        var input = new ManifestInput
        {
            Path = path,
            FileSizeBytes = fileBytes.Length,
            Sha256 = sha256,
            ImageBase = imageBase,
            ImageSizeRva = imageSize,
            TextRva = textRva,
            Machine = MachineName(machine)
        };

        return new AcClientImage(view, input);
    }

    private static bool TryFindSection(
        byte[] fileBytes, int sectionTableOffset, int numSections, string name,
        out int rva, out int rawOffset, out int rawSize, out int virtualSize)
    {
        rva = rawOffset = rawSize = virtualSize = 0;
        for (int i = 0; i < numSections; i++)
        {
            int sectionOffset = sectionTableOffset + i * SectionHeaderSize;
            if (sectionOffset + SectionHeaderSize > fileBytes.Length)
                return false;

            string sectionName = ReadAsciiNullPadded(fileBytes, sectionOffset, SectionNameLength);
            if (!string.Equals(sectionName, name, StringComparison.Ordinal))
                continue;

            virtualSize = BitConverter.ToInt32(fileBytes, sectionOffset + 0x08);
            rva = BitConverter.ToInt32(fileBytes, sectionOffset + 0x0C);
            rawSize = BitConverter.ToInt32(fileBytes, sectionOffset + 0x10);
            rawOffset = BitConverter.ToInt32(fileBytes, sectionOffset + 0x14);
            return true;
        }

        return false;
    }

    private static string ReadAsciiNullPadded(byte[] data, int offset, int length)
    {
        int end = offset;
        while (end < offset + length && data[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(data, offset, end - offset);
    }

    private static string ComputeSha256(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    private static string MachineName(ushort machine) => machine switch
    {
        0x014C => "x86",
        0x8664 => "x64",
        0x01C4 => "arm",
        0xAA64 => "arm64",
        _ => $"unknown(0x{machine:X4})"
    };
}
