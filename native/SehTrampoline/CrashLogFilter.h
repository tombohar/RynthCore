/*
 * CrashLogFilter.h - which exceptions the native crash logger (SehTrampoline.c) writes.
 *
 * Pure decision logic: no Win32 calls, no globals. The logger keeps one clf_state and
 * calls these under its own lock; native\SehTrampoline\tests\CrashLogFilterTests.c
 * runs them directly.
 *
 * Why: the logger used to write every first-chance AV until a cap of 24 per process.
 * Decal's .NET Framework plugins raise a handled null-reference AV in JIT code (clr.dll
 * and mscorlib frames) about once a minute, the SAME address each time, so the cap was
 * spent within minutes: one client's 24 entries (all within about five minutes) were all
 * exAddr 0x433620C8, and its fatal AV at 20:25 under GameTickHooks.UseTimeDetour left
 * nothing in native-crash.log.
 *
 * Now:
 *   - Logged codes: access violation, fail-fast, stack buffer overrun, stack overflow,
 *     heap corruption.
 *   - Each faulting address has a slot with its hit count and the time it was last
 *     written. The first sighting is written. A repeat is written again (with its count)
 *     when the count is a power of two (any class), or, for an AC-class address only,
 *     when 30 s have passed since it was last written, or when the code is not an AV.
 *     So a contained AV that later recurs as the fatal one is not swallowed as "seen
 *     before": a fatal fault is usually a terminate from some handler, and then no
 *     last-chance filter ever runs.
 *     Review fix (2026-10-18): the 30 s relog used to apply to noise too, so Decal's
 *     handled null-ref AV (about once a minute, one address) was written on EVERY hit:
 *     ~1,440 entries (~5-6 KB each with the stack sweep) per day per client. Noise now
 *     repeats only at powers of two (about 11 entries a day for that address), and the
 *     30 s / non-AV relogs are capped per address (CLF_MAX_RELOGS); what a cap or the
 *     dedupe skips is still in the flush ring, so a fatal hit is recovered at exit.
 *   - A total byte budget (CLF_MAX_LOG_BYTES, counted from the file's size at install plus
 *     what this process writes): past it ordinary entries are dropped after ONE marker
 *     line; fatal entries (last-chance, crash-exit flush) get CLF_LOG_RESERVE_BYTES more.
 *   - Noise is classified, written once per address like the rest, and never charged to
 *     the AC limit:
 *       .NET Framework (Decal plugins): faulting module clr.dll, mscorwks.dll, mscorlib;
 *       managed (the engine's CoreCLR):  coreclr.dll, clrjit.dll, System.Private.CoreLib;
 *     or no module at all (JIT code) with the nearest module-resolved code pointer on the
 *     stack in one of those. Only AVs are ever noise.
 *   - A first sighting of anything else (AC's faults) is charged to CLF_MAX_COUNTED, a
 *     flood guard on DISTINCT addresses only.
 *   - The last-chance (unhandled) entry is never charged to that limit and has its own
 *     small one (it happens once in a dying process; the limit only stops a loop).
 *   - A full address table evicts its least recently written NOISE slot. With no noise
 *     slot to evict, an AC address is still written (under the limit), just not tracked.
 *   - The last CLF_RING first-chance records, written or not, are kept in a ring (no I/O
 *     at exception time). A flush writes those that were not written one by one: the
 *     last-chance filter flushes, and so does the engine's crash-exit path (TerminateProcess
 *     and friends, through RC_FlushCrashLog). That recovers the case the dedupe alone
 *     cannot: the fatal hit of an address seen before, at a count that is not due a
 *     write, followed by a terminate from a handler, so no last-chance filter ever runs
 *     (checked with 5, 100 and 1000 contained hits before the fatal one).
 *
 * Times are GetTickCount milliseconds (32-bit; unsigned subtraction survives the wrap).
 */
#ifndef RYNTHCORE_CRASH_LOG_FILTER_H
#define RYNTHCORE_CRASH_LOG_FILTER_H

#define CLF_MAX_COUNTED      96
#define CLF_MAX_LAST_CHANCE   8
#define CLF_ADDR_SLOTS      128
#define CLF_RELOG_MS      30000u
#define CLF_MAX_RELOGS       16        /* non-power-of-two rewrites per address */
#define CLF_MAX_LOG_BYTES    (3u * 1024u * 1024u)   /* ordinary entries stop here */
#define CLF_LOG_RESERVE_BYTES (256u * 1024u)        /* extra room for fatal entries */

#define CLF_CODE_ACCESS_VIOLATION 0xC0000005u
#define CLF_CODE_FAIL_FAST        0xC0000602u
#define CLF_CODE_STACK_BUFFER     0xC0000409u
#define CLF_CODE_STACK_OVERFLOW   0xC00000FDu
#define CLF_CODE_HEAP_CORRUPTION  0xC0000374u

/* What raised a fault. Anything but CLF_CLASS_AC is noise. */
typedef enum {
    CLF_CLASS_AC = 0,        /* AC, Decal native code, the engine's native code: counted */
    CLF_CLASS_NETFX = 1,     /* the .NET Framework (Decal's managed plugins) */
    CLF_CLASS_MANAGED = 2,   /* the engine's CoreCLR runtime and its JIT code */
} clf_class;

typedef enum {
    CLF_SKIP_CODE = 0,       /* not a code the logger writes */
    CLF_LOG_COUNTED,         /* write it; first sighting, charged to the limit */
    CLF_LOG_NOISE,           /* write it; first sighting of noise, not charged */
    CLF_LOG_REPEAT,          /* write it again; a repeat (see hits) */
    CLF_LOG_LAST_CHANCE,     /* write it; unhandled */
    CLF_SKIP_REPEAT,         /* a repeat not due to be written */
    CLF_SKIP_CAP,            /* limit reached */
} clf_decision;

typedef struct {
    unsigned int addr;
    unsigned int hits;           /* sightings, the first included */
    unsigned int lastWriteMs;
    unsigned char cls;           /* clf_class */
    unsigned char used;
    unsigned char relogs;        /* writes that were not first / power-of-two (time, non-AV) */
} clf_slot;

typedef struct {
    clf_slot slots[CLF_ADDR_SLOTS];
    unsigned int counted;        /* AC first sightings charged to the limit */
    unsigned int lastChance;
    unsigned int noiseRepeats;   /* noise repeats not written */
    unsigned int otherRepeats;   /* other repeats not written */
    unsigned int overCap;        /* not written: limit reached */
    unsigned int untracked;      /* AC addresses written but not tracked (table full of AC) */
    unsigned int relogCapped;    /* relogs skipped: the address used its CLF_MAX_RELOGS */
    unsigned int logBytes;       /* bytes in the log file: size at install + written since */
    unsigned int byteDenied;     /* entries dropped by the byte budget */
    unsigned char capMarked;     /* the budget marker line has been written */
} clf_state;

static int clf_is_logged_code(unsigned int code)
{
    return code == CLF_CODE_ACCESS_VIOLATION || code == CLF_CODE_FAIL_FAST
        || code == CLF_CODE_STACK_BUFFER || code == CLF_CODE_STACK_OVERFLOW
        || code == CLF_CODE_HEAP_CORRUPTION;
}

static int clf_wide_ieq(const wchar_t* a, const char* b)
{
    if (!a || !b) return 0;
    for (;;) {
        wchar_t ca = *a++;
        char cb = *b++;
        if (ca >= L'A' && ca <= L'Z') ca = (wchar_t)(ca - L'A' + L'a');
        if (cb >= 'A' && cb <= 'Z') cb = (char)(cb - 'A' + 'a');
        if (ca != (wchar_t)(unsigned char)cb) return 0;
        if (ca == 0) return 1;
    }
}

/* Class of a module, by its leaf file name. */
static clf_class clf_module_class(const wchar_t* leaf)
{
    if (clf_wide_ieq(leaf, "clr.dll") || clf_wide_ieq(leaf, "mscorwks.dll")
        || clf_wide_ieq(leaf, "mscorlib.dll") || clf_wide_ieq(leaf, "mscorlib.ni.dll"))
        return CLF_CLASS_NETFX;
    if (clf_wide_ieq(leaf, "coreclr.dll") || clf_wide_ieq(leaf, "clrjit.dll")
        || clf_wide_ieq(leaf, "System.Private.CoreLib.dll"))
        return CLF_CLASS_MANAGED;
    return CLF_CLASS_AC;
}

/*
 * Class of a first-chance exception. faultClass: class of the faulting module (AC when in
 * no module). faultInNoModule: the faulting address is in no module (JIT code).
 * firstFrameClass: class of the nearest module-resolved code pointer on the stack.
 */
static clf_class clf_classify(unsigned int code, clf_class faultClass, int faultInNoModule, clf_class firstFrameClass)
{
    if (code != CLF_CODE_ACCESS_VIOLATION) return CLF_CLASS_AC;
    if (faultClass != CLF_CLASS_AC) return faultClass;
    if (faultInNoModule) return firstFrameClass;
    return CLF_CLASS_AC;
}

static clf_slot* clf_find(clf_state* s, unsigned int addr)
{
    int i;
    for (i = 0; i < CLF_ADDR_SLOTS; i++)
        if (s->slots[i].used && s->slots[i].addr == addr) return &s->slots[i];
    return 0;
}

static int clf_is_pow2(unsigned int v) { return v != 0 && (v & (v - 1)) == 0; }

/* A repeat at a tracked address. *hits gets the sighting count. */
static clf_decision clf_repeat(clf_state* s, clf_slot* slot, unsigned int code, unsigned int nowMs, unsigned int* hits)
{
    slot->hits++;
    if (hits) *hits = slot->hits;
    if (clf_is_pow2(slot->hits)) {
        slot->lastWriteMs = nowMs;
        return CLF_LOG_REPEAT;
    }
    /* The 30 s relog is for genuine (AC-class) faults only: noise at one address (Decal's
     * handled null-ref, about once a minute) would otherwise be written on every hit. A
     * non-AV code is always written, within the per-address cap. */
    if ((slot->cls == CLF_CLASS_AC && nowMs - slot->lastWriteMs >= CLF_RELOG_MS)
        || code != CLF_CODE_ACCESS_VIOLATION) {
        if (slot->relogs < CLF_MAX_RELOGS) {
            slot->relogs++;
            slot->lastWriteMs = nowMs;
            return CLF_LOG_REPEAT;
        }
        s->relogCapped++;
    }
    if (slot->cls != CLF_CLASS_AC) s->noiseRepeats++; else s->otherRepeats++;
    return CLF_SKIP_REPEAT;
}

/* A free slot, else the least recently written noise slot, else none. */
static clf_slot* clf_take_slot(clf_state* s)
{
    clf_slot* oldest = 0;
    int i;
    for (i = 0; i < CLF_ADDR_SLOTS; i++)
        if (!s->slots[i].used) return &s->slots[i];
    for (i = 0; i < CLF_ADDR_SLOTS; i++) {
        clf_slot* c = &s->slots[i];
        if (c->cls == CLF_CLASS_AC) continue;
        if (!oldest || (int)(c->lastWriteMs - oldest->lastWriteMs) < 0) oldest = c;
    }
    return oldest;
}

/* First sighting of an address (clf_find found nothing). */
static clf_decision clf_new(clf_state* s, unsigned int code, unsigned int addr, clf_class cls, unsigned int nowMs)
{
    clf_slot* slot;
    if (cls == CLF_CLASS_AC) {
        if (s->counted >= CLF_MAX_COUNTED) { s->overCap++; return CLF_SKIP_CAP; }
        s->counted++;
    }
    slot = clf_take_slot(s);
    if (slot) {
        slot->addr = addr;
        slot->hits = 1;
        slot->lastWriteMs = nowMs;
        slot->cls = (unsigned char)cls;
        slot->relogs = 0;
        slot->used = 1;
    } else {
        s->untracked++;
    }
    (void)code;
    return cls == CLF_CLASS_AC ? CLF_LOG_COUNTED : CLF_LOG_NOISE;
}

/* The whole first-chance decision in one call (the logger splits it to skip the module
 * lookup for repeats). *hits gets the sighting count (1 for a first sighting). */
static clf_decision clf_first_chance(clf_state* s, unsigned int code, unsigned int addr, clf_class cls,
                                     unsigned int nowMs, unsigned int* hits)
{
    clf_slot* slot;
    if (hits) *hits = 1;
    if (!clf_is_logged_code(code)) return CLF_SKIP_CODE;
    slot = clf_find(s, addr);
    if (slot) return clf_repeat(s, slot, code, nowMs, hits);
    return clf_new(s, code, addr, cls, nowMs);
}

#define CLF_RING 16

/* One first-chance record kept for a later flush. */
typedef struct {
    unsigned int seq;            /* 1-based push number; 0 = empty */
    unsigned int addr, code, eip, tid, tickMs, hits;
    unsigned char written;       /* written as an entry of its own when it happened */
} clf_rec;

/*
 * The last CLF_RING first-chance records. Each record is offered to one flush only:
 * a second flush (one exit path calling another) writes nothing new. Sequence numbers
 * are 32-bit; a process would need 4 billion logged-code exceptions to wrap them.
 */
typedef struct {
    clf_rec r[CLF_RING];
    unsigned int pushed;         /* records pushed in total */
    unsigned int flushedTo;      /* every record up to this seq has been offered to a flush */
} clf_ring;

static void clf_ring_push(clf_ring* g, unsigned int addr, unsigned int code, unsigned int eip,
                          unsigned int tid, unsigned int tickMs, unsigned int hits, int written)
{
    clf_rec* x = &g->r[g->pushed % CLF_RING];
    g->pushed++;
    x->seq = g->pushed;
    x->addr = addr;
    x->code = code;
    x->eip = eip;
    x->tid = tid;
    x->tickMs = tickMs;
    x->hits = hits;
    x->written = (unsigned char)(written ? 1 : 0);
}

/*
 * Flush: copies into out[CLF_RING], oldest first, the held records that were not written
 * and not offered to an earlier flush, and marks everything pushed so far as offered.
 * Returns how many were copied. *older gets the records pushed since the last flush that
 * the ring no longer holds (written or not).
 */
static int clf_ring_take(clf_ring* g, clf_rec* out, unsigned int* older)
{
    unsigned int first = g->pushed > CLF_RING ? g->pushed - CLF_RING : 0;   /* held: first+1..pushed */
    unsigned int from = g->flushedTo, s;
    int n = 0;
    if (older) *older = from < first ? first - from : 0;
    if (from < first) from = first;
    for (s = from + 1; s <= g->pushed; s++) {
        const clf_rec* x = &g->r[(s - 1) % CLF_RING];
        if (!x->written) out[n++] = *x;
    }
    g->flushedTo = g->pushed;
    return n;
}

typedef enum {
    CLF_BYTES_OK = 0,        /* write the entry */
    CLF_BYTES_MARK,          /* over the budget: write the one marker line, then no entry */
    CLF_BYTES_DENY,          /* over the budget: write nothing */
} clf_bytes;

/*
 * The byte budget, asked before each entry. fatal: a last-chance / crash-exit entry,
 * which may use CLF_LOG_RESERVE_BYTES beyond CLF_MAX_LOG_BYTES. The caller adds what it
 * writes to s->logBytes (the marker included).
 */
static clf_bytes clf_bytes_gate(clf_state* s, int fatal)
{
    unsigned int limit = CLF_MAX_LOG_BYTES + (fatal ? CLF_LOG_RESERVE_BYTES : 0u);
    if (s->logBytes < limit) return CLF_BYTES_OK;
    s->byteDenied++;
    if (!fatal && !s->capMarked) { s->capMarked = 1; return CLF_BYTES_MARK; }
    return CLF_BYTES_DENY;
}

/* The last-chance (unhandled) filter. Any code: whatever reaches it is fatal. */
static clf_decision clf_last_chance(clf_state* s)
{
    if (s->lastChance >= CLF_MAX_LAST_CHANCE) return CLF_SKIP_CAP;
    s->lastChance++;
    return CLF_LOG_LAST_CHANCE;
}

#endif
