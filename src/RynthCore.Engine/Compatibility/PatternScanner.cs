using System;

namespace RynthCore.Engine.Compatibility;

internal static class PatternScanner
{
    public static int FindPattern(byte[] data, byte[] pattern)
    {
        return FindPatternInRegion(data, pattern, 0, data.Length);
    }

    public static int FindPattern(byte[] data, byte?[] pattern)
    {
        return FindPatternInRegion(data, pattern, 0, data.Length);
    }

    public static int FindPatternInRegion(byte[] data, byte[] pattern, int start, int end)
    {
        int limit = Math.Min(end, data.Length) - pattern.Length;
        for (int i = Math.Max(0, start); i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] == pattern[j])
                    continue;

                match = false;
                break;
            }

            if (match)
                return i;
        }

        return -1;
    }

    public static int FindPatternInRegion(byte[] data, byte?[] pattern, int start, int end)
    {
        int limit = Math.Min(end, data.Length) - pattern.Length;
        for (int i = Math.Max(0, start); i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                byte? expected = pattern[j];
                if (expected.HasValue && data[i + j] != expected.Value)
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }

    public static int FindPrologueBefore(byte[] data, int opcodeOffset, byte[] prologue, int maxDistance = 300)
    {
        for (int back = 1; back < maxDistance; back++)
        {
            int pos = opcodeOffset - back;
            if (pos < 1 || pos + prologue.Length > data.Length)
                continue;

            if (!VerifyBytes(data, pos, prologue))
                continue;

            byte prev = data[pos - 1];
            if (prev == 0xCC || prev == 0x90 || prev == 0xC3)
                return pos;
        }

        return -1;
    }

    public static bool VerifyBytes(byte[] data, int offset, byte[] expected)
    {
        if (offset < 0 || offset + expected.Length > data.Length)
            return false;

        for (int i = 0; i < expected.Length; i++)
        {
            if (data[offset + i] != expected[i])
                return false;
        }

        return true;
    }

    public static bool VerifyPattern(byte[] data, int offset, byte?[] expected)
    {
        if (offset < 0 || offset + expected.Length > data.Length)
            return false;

        for (int i = 0; i < expected.Length; i++)
        {
            byte? value = expected[i];
            if (value.HasValue && data[offset + i] != value.Value)
                return false;
        }

        return true;
    }
}
