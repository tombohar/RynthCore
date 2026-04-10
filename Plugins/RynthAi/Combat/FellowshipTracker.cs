using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Decal.Adapter;

namespace NexSuite.Plugins.RynthAi
{
    /// <summary>
    /// Reads fellowship membership directly from AC client memory.
    /// 
    /// Memory layout (from UB's AcClient structs / acclient.exe analysis):
    /// 
    /// Static pointers:
    ///   0x0087150C = ClientFellowshipSystem** s_pFellowshipSystem
    ///   0x00844C08 = UInt32* player_iid (our character ID)
    /// 
    /// ClientFellowshipSystem:
    ///   +0x10 = CFellowship* m_pFellowship (null if not in fellowship)
    /// 
    /// CFellowship / Fellowship:
    ///   +0x0C = PackableHashData** _buckets (hash table bucket array)
    ///   +0x10 = UInt32 _table_size (number of buckets)
    ///   +0x14 = UInt32 _currNum (member count)
    ///   +0x18 = PStringBase _name (fellowship name, char*)
    ///   +0x1C = UInt32 _leader (leader character ID)
    ///   +0x20 = int _share_xp
    ///   +0x24 = int _even_xp_split
    ///   +0x28 = int _open_fellow
    ///   +0x2C = int _locked
    /// 
    /// PackableHashData (hash table entry for each member):
    ///   +0x00 = UInt32 _key (member character ID)
    ///   +0x04 = Fellow _data:
    ///       +0x04 = Fellow.PackObj vtable (4 bytes)
    ///       +0x08 = Fellow._name (PStringBase = char*, 4 bytes)
    ///       +0x0C = Fellow._level (UInt32)
    ///       +0x1C = Fellow._share_loot (int)
    ///       +0x20 = Fellow._max_health (UInt32)
    ///       +0x28 = Fellow._max_mana (UInt32)
    ///       +0x2C = Fellow._current_health (UInt32)
    ///   +0x34 = PackableHashData* _next (next in chain, null = end)
    /// </summary>
    public class FellowshipTracker : IDisposable
    {
        // Static memory addresses in acclient.exe
        private static readonly IntPtr ADDR_FELLOWSHIP_SYSTEM = new IntPtr(0x0087150C);
        private static readonly IntPtr ADDR_PLAYER_IID = new IntPtr(0x00844C08);

        // Struct offsets
        private const int OFF_SYS_FELLOWSHIP = 0x10;   // ClientFellowshipSystem → m_pFellowship
        private const int OFF_FEL_BUCKETS    = 0x0C;   // Fellowship → _buckets
        private const int OFF_FEL_TABLE_SIZE = 0x10;   // Fellowship → _table_size
        private const int OFF_FEL_CURR_NUM   = 0x14;   // Fellowship → _currNum
        private const int OFF_FEL_NAME       = 0x18;   // Fellowship → _name (char*)
        private const int OFF_FEL_LEADER     = 0x1C;   // Fellowship → _leader
        private const int OFF_FEL_SHARE_XP   = 0x20;   // Fellowship → _share_xp
        private const int OFF_FEL_EVEN_SPLIT = 0x24;   // Fellowship → _even_xp_split
        private const int OFF_FEL_OPEN       = 0x28;   // Fellowship → _open_fellow
        private const int OFF_FEL_LOCKED     = 0x2C;   // Fellowship → _locked

        // Hash entry offsets (PackableHashData<UInt32, Fellow>)
        private const int OFF_ENTRY_KEY      = 0x00;   // _key (member character ID)
        private const int OFF_ENTRY_NAME_PTR = 0x08;   // Fellow._name (char*)
        private const int OFF_ENTRY_LEVEL    = 0x0C;   // Fellow._level
        private const int OFF_ENTRY_NEXT     = 0x34;   // _next pointer

        // Cached member data
        private Dictionary<int, string> _memberCache = new Dictionary<int, string>();
        private DateTime _lastRefresh = DateTime.MinValue;
        private const double REFRESH_INTERVAL_MS = 2000;

        private CoreManager _core;

        public FellowshipTracker(CoreManager core)
        {
            _core = core;
        }

        public void Dispose() { }

        // ══════════════════════════════════════════════════════════════
        //  PUBLIC API — matches the UB expression interface
        // ══════════════════════════════════════════════════════════════

        /// <summary>True if the player is currently in a fellowship.</summary>
        public bool IsInFellowship
        {
            get
            {
                try { return GetFellowshipPtr() != IntPtr.Zero; }
                catch { return false; }
            }
        }

        /// <summary>Number of members in the current fellowship.</summary>
        public int MemberCount
        {
            get
            {
                try
                {
                    IntPtr fel = GetFellowshipPtr();
                    if (fel == IntPtr.Zero) return 0;
                    return Marshal.ReadInt32(fel + OFF_FEL_CURR_NUM);
                }
                catch { return 0; }
            }
        }

        /// <summary>Name of the current fellowship.</summary>
        public string FellowshipName
        {
            get
            {
                try
                {
                    IntPtr fel = GetFellowshipPtr();
                    if (fel == IntPtr.Zero) return "";
                    return ReadPString(fel + OFF_FEL_NAME);
                }
                catch { return ""; }
            }
        }

        /// <summary>Character ID of the fellowship leader.</summary>
        public int LeaderId
        {
            get
            {
                try
                {
                    IntPtr fel = GetFellowshipPtr();
                    if (fel == IntPtr.Zero) return 0;
                    return Marshal.ReadInt32(fel + OFF_FEL_LEADER);
                }
                catch { return 0; }
            }
        }

        /// <summary>True if the player is the fellowship leader.</summary>
        public bool IsLeader
        {
            get
            {
                try
                {
                    int leader = LeaderId;
                    if (leader == 0) return false;
                    int myId = Marshal.ReadInt32(ADDR_PLAYER_IID);
                    return leader == myId;
                }
                catch { return false; }
            }
        }

        /// <summary>True if the fellowship is open for recruitment.</summary>
        public bool IsOpen
        {
            get
            {
                try
                {
                    IntPtr fel = GetFellowshipPtr();
                    if (fel == IntPtr.Zero) return false;
                    return Marshal.ReadInt32(fel + OFF_FEL_OPEN) == 1;
                }
                catch { return false; }
            }
        }

        /// <summary>True if the fellowship is locked.</summary>
        public bool IsLocked
        {
            get
            {
                try
                {
                    IntPtr fel = GetFellowshipPtr();
                    if (fel == IntPtr.Zero) return false;
                    return Marshal.ReadInt32(fel + OFF_FEL_LOCKED) == 1;
                }
                catch { return false; }
            }
        }

        /// <summary>True if the fellowship is sharing XP.</summary>
        public bool ShareXP
        {
            get
            {
                try
                {
                    IntPtr fel = GetFellowshipPtr();
                    if (fel == IntPtr.Zero) return false;
                    return Marshal.ReadInt32(fel + OFF_FEL_SHARE_XP) == 1;
                }
                catch { return false; }
            }
        }

        /// <summary>Check if a character name is a fellowship member.</summary>
        public bool IsMember(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            RefreshIfNeeded();
            foreach (var kvp in _memberCache)
            {
                if (kvp.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Check if a character ID is a fellowship member.</summary>
        public bool IsMember(int characterId)
        {
            RefreshIfNeeded();
            return _memberCache.ContainsKey(characterId);
        }

        /// <summary>Get all member names.</summary>
        public IEnumerable<string> GetMemberNames()
        {
            RefreshIfNeeded();
            return _memberCache.Values;
        }

        /// <summary>Get member name by index (0-based).</summary>
        public string GetMemberName(int index)
        {
            RefreshIfNeeded();
            int i = 0;
            foreach (var kvp in _memberCache)
            {
                if (i == index) return kvp.Value;
                i++;
            }
            return "";
        }

        /// <summary>Get member character ID by index (0-based).</summary>
        public int GetMemberId(int index)
        {
            RefreshIfNeeded();
            int i = 0;
            foreach (var kvp in _memberCache)
            {
                if (i == index) return kvp.Key;
                i++;
            }
            return 0;
        }

        // ══════════════════════════════════════════════════════════════
        //  NATIVE MEMORY READING
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Gets the CFellowship pointer. Returns IntPtr.Zero if not in a fellowship.
        /// Chain: [s_pFellowshipSystem] → ClientFellowshipSystem → [+0x10] → CFellowship
        /// </summary>
        private IntPtr GetFellowshipPtr()
        {
            int sysPtr = Marshal.ReadInt32(ADDR_FELLOWSHIP_SYSTEM);
            if (sysPtr == 0) return IntPtr.Zero;

            int felPtr = Marshal.ReadInt32(new IntPtr(sysPtr + OFF_SYS_FELLOWSHIP));
            if (felPtr == 0) return IntPtr.Zero;

            return new IntPtr(felPtr);
        }

        /// <summary>
        /// Refreshes the member cache by iterating the hash table in client memory.
        /// Only refreshes every REFRESH_INTERVAL_MS to avoid overhead.
        /// </summary>
        private void RefreshIfNeeded()
        {
            if ((DateTime.Now - _lastRefresh).TotalMilliseconds < REFRESH_INTERVAL_MS)
                return;
            _lastRefresh = DateTime.Now;

            _memberCache.Clear();

            try
            {
                IntPtr fel = GetFellowshipPtr();
                if (fel == IntPtr.Zero) return;

                int bucketsPtr = Marshal.ReadInt32(fel + OFF_FEL_BUCKETS);
                int tableSize  = Marshal.ReadInt32(fel + OFF_FEL_TABLE_SIZE);
                int currNum    = Marshal.ReadInt32(fel + OFF_FEL_CURR_NUM);

                if (bucketsPtr == 0 || tableSize == 0 || currNum == 0) return;

                // Safety: don't iterate more than 9 members (AC fellowship max)
                int maxMembers = Math.Min(currNum, 9);
                int found = 0;

                // Walk each bucket in the hash table
                for (int b = 0; b < tableSize && found < maxMembers; b++)
                {
                    // Read bucket pointer: _buckets[b]
                    int entryPtr = Marshal.ReadInt32(new IntPtr(bucketsPtr + b * 4));

                    // Walk the chain for this bucket
                    while (entryPtr != 0 && found < maxMembers)
                    {
                        // Read _key (member character ID) at entry + 0x00
                        int memberId = Marshal.ReadInt32(new IntPtr(entryPtr + OFF_ENTRY_KEY));

                        // Read _name pointer at entry + 0x08 (Fellow._name = char*)
                        string name = ReadPString(new IntPtr(entryPtr + OFF_ENTRY_NAME_PTR));

                        if (memberId != 0 && !string.IsNullOrEmpty(name))
                        {
                            _memberCache[memberId] = name;
                            found++;
                        }

                        // Follow _next pointer at entry + 0x34
                        entryPtr = Marshal.ReadInt32(new IntPtr(entryPtr + OFF_ENTRY_NEXT));
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Reads a PStringBase&lt;char&gt; from a memory address.
        /// 
        /// PStringBase contains a single m_buffer pointer to PSRefBuffer&lt;char&gt;.
        /// PSRefBuffer layout (verified from acclient.exe binary analysis —
        /// PStringBase ctor at 0x48C3E0 copies chars to buf+0x14,
        /// allocation helper at 0x403560 initializes the header):
        ///   +0x00: vtable     (ReferenceCountTemplate vftable, e.g. 0x7CAE34)
        ///   +0x04: m_cRef     (int32 — reference count)
        ///   +0x08: m_Length   (int32 — string length INCLUDING null terminator)
        ///   +0x0C: m_Capacity (int32 — allocated buffer capacity)
        ///   +0x10: m_Hash     (int32 — initialized to 0xFFFFFFFF)
        ///   +0x14: char[]     (actual string data, null-terminated)
        /// </summary>
        private string ReadPString(IntPtr addr)
        {
            try
            {
                // Step 1: Read PSRefBuffer* from PStringBase.m_buffer
                int bufPtr = Marshal.ReadInt32(addr);
                if (bufPtr == 0) return "";

                // Step 2: Read string data from PSRefBuffer + 0x14 (after 20-byte header)
                // Use null-terminated read with safety cap at 64 chars
                string raw = Marshal.PtrToStringAnsi(new IntPtr(bufPtr + 0x14));
                if (raw == null) return "";
                if (raw.Length > 64) raw = raw.Substring(0, 64);
                return raw;
            }
            catch { return ""; }
        }
    }
}
