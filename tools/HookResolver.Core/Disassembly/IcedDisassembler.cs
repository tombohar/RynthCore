using System;
using System.Collections.Generic;
using Iced.Intel;
using RynthCore.HookManifest.Scanning;
using RynthCore.HookManifest.Schema;

namespace RynthCore.HookResolver.Core.Disassembly;

public static class IcedDisassembler
{
    private const int DefaultMaxBytes = 0x1000;
    private const int DefaultMaxInstructions = 4096;

    public static FunctionSnapshot Snapshot(
        AcClientImageView image,
        int rva,
        int maxBytes = DefaultMaxBytes,
        int maxInstructions = DefaultMaxInstructions,
        int maxCallsToCapture = 8)
    {
        int textRva = image.TextRva;
        int textOffset = rva - textRva;
        if (textOffset < 0 || textOffset >= image.TextBytes.Length)
        {
            return new FunctionSnapshot
            {
                Rva = rva,
                Va = image.ImageBase + rva,
                LengthBytes = 0,
                InstructionCount = 0,
                EndsAtReturn = false,
                PrologueHex = "",
                Note = "RVA outside .text"
            };
        }

        int available = image.TextBytes.Length - textOffset;
        int scanLen = Math.Min(maxBytes, available);

        byte[] slice = new byte[scanLen];
        Array.Copy(image.TextBytes, textOffset, slice, 0, scanLen);

        var reader = new ByteArrayCodeReader(slice);
        var decoder = Decoder.Create(32, reader);
        decoder.IP = (ulong)(uint)(image.ImageBase + rva);

        int firstVa = image.ImageBase + rva;
        ulong endIp = (ulong)(uint)(firstVa + scanLen);

        int instructionCount = 0;
        bool endsAtReturn = false;
        var calls = new List<CallTarget>();

        while (decoder.IP < endIp && instructionCount < maxInstructions)
        {
            decoder.Decode(out var instr);
            if (instr.IsInvalid)
                break;

            instructionCount++;

            if (instr.Mnemonic == Mnemonic.Call &&
                instr.OpCount > 0 &&
                instr.Op0Kind == OpKind.NearBranch32 &&
                calls.Count < maxCallsToCapture)
            {
                int targetVa = (int)(uint)instr.NearBranch32;
                int offset = (int)(instr.IP - (ulong)(uint)firstVa);
                calls.Add(new CallTarget(targetVa, offset));
            }

            if (instr.FlowControl == FlowControl.Return)
            {
                endsAtReturn = true;
                break;
            }

            if (instr.FlowControl == FlowControl.UnconditionalBranch && instructionCount > 2)
            {
                break;
            }
        }

        int finalLength = (int)((long)decoder.IP - firstVa);
        int prologueLen = Math.Min(8, scanLen);
        string prologueHex = Convert.ToHexString(image.TextBytes.AsSpan(textOffset, prologueLen));

        return new FunctionSnapshot
        {
            Rva = rva,
            Va = firstVa,
            LengthBytes = finalLength,
            InstructionCount = instructionCount,
            EndsAtReturn = endsAtReturn,
            PrologueHex = prologueHex,
            CallTargets = calls
        };
    }
}
