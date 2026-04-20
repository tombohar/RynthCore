using System;
using System.Collections.Generic;

namespace RynthCore.Engine.Compatibility;

// Stores per-object ObjDesc subpalette data captured in CreateObjectDetour.
// ObjDesc (x86 layout, all 4-byte fields):
//   +0  vtable ptr (VisualDesc.packObj)
//   +4  paletteID
//   +8  firstSubpal*
//   +12 lastSubpal*
//   +16 num_subpalettes
// Subpalette (x86):
//   +0  vtable ptr (PackObj)
//   +4  subID   ← palette DID
//   +8  offset  ← range start (sorted key)
//   +12 numcolors
//   +16 prev*
//   +20 next*
internal static unsafe class PaletteCache
{
    private static readonly Dictionary<uint, (uint SubId, uint Offset)[]> _data = new();
    private static readonly object _lock = new();

    public static void ReadFromObjDesc(uint objectId, IntPtr objDescPtr)
    {
        if (objDescPtr == IntPtr.Zero) return;
        try
        {
            byte* p = (byte*)objDescPtr;
            int numSubs = *(int*)(p + 16);
            if (numSubs <= 0 || numSubs > 128) return;

            var list = new (uint SubId, uint Offset)[numSubs];
            IntPtr node = *(IntPtr*)(p + 8);
            int idx = 0;
            while (node != IntPtr.Zero && idx < numSubs)
            {
                byte* s = (byte*)node;
                list[idx++] = (*(uint*)(s + 4), *(uint*)(s + 8));
                node = *(IntPtr*)(s + 20);
            }
            if (idx != numSubs)
                Array.Resize(ref list, idx);
            Array.Sort(list, (a, b) => a.Offset.CompareTo(b.Offset));
            lock (_lock)
                _data[objectId] = list;
        }
        catch { }
    }

    public static void Remove(uint objectId)
    {
        lock (_lock)
            _data.Remove(objectId);
    }

    public static int Fill(uint objectId, uint* subIds, uint* offsets, int maxCount)
    {
        (uint SubId, uint Offset)[]? list;
        lock (_lock)
        {
            if (!_data.TryGetValue(objectId, out list)) return -1;
        }
        int count = Math.Min(list.Length, maxCount);
        for (int i = 0; i < count; i++)
        {
            subIds[i] = list[i].SubId;
            offsets[i] = list[i].Offset;
        }
        return list.Length;
    }
}
