/*
 * CrashLogFilterTests.c - offline checks for CrashLogFilter.h (the native crash logger's
 * filter). Build and run with Run-CrashLogFilterTests.ps1 (x86 MSVC). Exit 0 = pass.
 */
#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include "../CrashLogFilter.h"

static int g_checks, g_failed;

static void check(int ok, const char* what)
{
    g_checks++;
    if (ok) return;
    g_failed++;
    printf("[FAIL] %s\n", what);
}

#define AV CLF_CODE_ACCESS_VIOLATION
#define FC(s, code, addr, cls, now) clf_first_chance((s), (code), (addr), (cls), (now), &hits)

static int is_write(clf_decision d)
{
    return d == CLF_LOG_COUNTED || d == CLF_LOG_NOISE || d == CLF_LOG_REPEAT;
}

/* Seen in a real log: handled .NET Framework AVs at ONE address spent the old cap,
 * and the fatal AC fault later was never written. */
static void netfx_noise_never_spends_the_limit(void)
{
    static clf_state s;
    unsigned int hits, i, written = 0;
    for (i = 0; i < 500; i++)
        if (is_write(FC(&s, AV, 0x433620C8u, CLF_CLASS_NETFX, 1000 + i * 10))) written++;
    /* first + the power-of-two repeats 2,4,8,16,32,64,128,256 = 9 entries for 500 hits */
    check(written == 9, "500 hits of one JIT-code AV: 9 entries (first + powers of two)");
    check(s.counted == 0, "and never charged to the limit");
    check(s.noiseRepeats == 491, "skipped repeats are counted as noise");
    check(FC(&s, AV, 0x0055FA24u, CLF_CLASS_AC, 7000) == CLF_LOG_COUNTED, "a later AC fault is still written");
}

/* A contained AV at a known address that later goes fatal. */
static void known_address_going_fatal_is_written(void)
{
    static clf_state s;
    unsigned int hits, i;
    clf_decision d;
    for (i = 0; i < 3; i++) FC(&s, AV, 0x00416C86u, CLF_CLASS_AC, 1000 + i);
    d = FC(&s, AV, 0x00416C86u, CLF_CLASS_AC, 1010);
    check(d == CLF_LOG_REPEAT && hits == 4, "3 contained AVs, then a 4th (fatal) at the same address: written, repeat=4");

    {
        static clf_state t;
        unsigned int now = 5000;
        for (i = 0; i < 1000; i++) FC(&t, AV, 0x00416C86u, CLF_CLASS_AC, now + i);   /* within 1 s */
        d = FC(&t, AV, 0x00416C86u, CLF_CLASS_AC, now + 1000 + 31000);
        check(d == CLF_LOG_REPEAT && hits == 1001, "1000 repeats, then the same address 31 s later: written");
        d = FC(&t, AV, 0x00416C86u, CLF_CLASS_AC, now + 1000 + 31001);
        check(d == CLF_SKIP_REPEAT, "and the next one right after is not");
    }
    {
        static clf_state u;
        FC(&u, AV, 0x00500000u, CLF_CLASS_AC, 100);
        FC(&u, AV, 0x00500000u, CLF_CLASS_AC, 101);
        FC(&u, AV, 0x00500000u, CLF_CLASS_AC, 102);   /* hits 3: skipped */
        d = FC(&u, CLF_CODE_HEAP_CORRUPTION, 0x00500000u, CLF_CLASS_AC, 103);
        check(d == CLF_LOG_REPEAT, "a non-AV code at a known address is always written");
    }
    {
        static clf_state w;   /* the 32-bit tick wrap */
        FC(&w, AV, 0x00600000u, CLF_CLASS_AC, 0xFFFFF000u);
        FC(&w, AV, 0x00600000u, CLF_CLASS_AC, 0xFFFFF001u);
        FC(&w, AV, 0x00600000u, CLF_CLASS_AC, 0xFFFFF002u);
        d = FC(&w, AV, 0x00600000u, CLF_CLASS_AC, 0x00007000u);   /* ~32 s later, past the wrap */
        check(d == CLF_LOG_REPEAT, "the 30 s relog survives the GetTickCount wrap");
    }
}

static void ac_faults_limited_per_distinct_address(void)
{
    static clf_state s;
    unsigned int hits, a;
    for (a = 0; a < 50; a++) FC(&s, AV, 0x00416C86u, CLF_CLASS_AC, 1000);
    check(s.counted == 1, "a repeating AC fault is charged once");
    for (a = 1; a < CLF_MAX_COUNTED; a++)
        check(FC(&s, AV, 0x00500000u + a * 16, CLF_CLASS_AC, 1000) == CLF_LOG_COUNTED,
              "distinct addresses written up to the limit");
    check(FC(&s, AV, 0x00700000u, CLF_CLASS_AC, 1000) == CLF_SKIP_CAP, "past the limit: skipped");
    check(FC(&s, AV, 0x00416C86u, CLF_CLASS_AC, 1000) != CLF_SKIP_CAP,
          "a known address is decided as a repeat, never by the limit");
}

static void codes(void)
{
    static clf_state s;
    unsigned int hits;
    check(FC(&s, 0xC00000FDu, 0x1000u, CLF_CLASS_AC, 1) == CLF_LOG_COUNTED, "stack overflow is logged");
    check(FC(&s, 0xC0000374u, 0x2000u, CLF_CLASS_AC, 1) == CLF_LOG_COUNTED, "heap corruption is logged");
    check(FC(&s, 0xC0000409u, 0x3000u, CLF_CLASS_AC, 1) == CLF_LOG_COUNTED, "stack buffer overrun is logged");
    check(FC(&s, 0xC0000602u, 0x4000u, CLF_CLASS_AC, 1) == CLF_LOG_COUNTED, "fail-fast is logged");
    check(FC(&s, 0xE06D7363u, 0x5000u, CLF_CLASS_AC, 1) == CLF_SKIP_CODE, "a C++ exception is not");
    check(FC(&s, 0x80000003u, 0x6000u, CLF_CLASS_AC, 1) == CLF_SKIP_CODE, "a breakpoint is not");
}

static void classification(void)
{
    check(clf_module_class(L"clr.dll") == CLF_CLASS_NETFX && clf_module_class(L"MSCORLIB.NI.DLL") == CLF_CLASS_NETFX
          && clf_module_class(L"mscorwks.dll") == CLF_CLASS_NETFX && clf_module_class(L"mscorlib.dll") == CLF_CLASS_NETFX,
          "the .NET Framework modules, any case");
    check(clf_module_class(L"coreclr.dll") == CLF_CLASS_MANAGED && clf_module_class(L"clrjit.dll") == CLF_CLASS_MANAGED
          && clf_module_class(L"System.Private.CoreLib.dll") == CLF_CLASS_MANAGED,
          "the engine's CoreCLR modules are managed noise");
    check(clf_module_class(L"acclient.exe") == CLF_CLASS_AC && clf_module_class(L"clr.dllx") == CLF_CLASS_AC
          && clf_module_class(0) == CLF_CLASS_AC, "AC and near-misses are not noise");
    check(clf_classify(AV, CLF_CLASS_NETFX, 0, CLF_CLASS_AC) == CLF_CLASS_NETFX, "an AV in clr.dll is .NET Framework noise");
    check(clf_classify(AV, CLF_CLASS_AC, 1, CLF_CLASS_NETFX) == CLF_CLASS_NETFX, "JIT code under clr frames: .NET Framework noise");
    check(clf_classify(AV, CLF_CLASS_AC, 1, CLF_CLASS_MANAGED) == CLF_CLASS_MANAGED, "JIT code under CoreCLR frames: managed noise");
    check(clf_classify(AV, CLF_CLASS_MANAGED, 0, CLF_CLASS_AC) == CLF_CLASS_MANAGED, "an AV in coreclr.dll: managed noise");
    check(clf_classify(AV, CLF_CLASS_AC, 1, CLF_CLASS_AC) == CLF_CLASS_AC, "no module under AC frames: AC");
    check(clf_classify(AV, CLF_CLASS_AC, 0, CLF_CLASS_NETFX) == CLF_CLASS_AC, "an AV in an AC module is AC whatever is below it");
    check(clf_classify(CLF_CODE_STACK_OVERFLOW, CLF_CLASS_NETFX, 0, CLF_CLASS_AC) == CLF_CLASS_AC, "a stack overflow is never noise");
    check(clf_classify(CLF_CODE_HEAP_CORRUPTION, CLF_CLASS_MANAGED, 0, CLF_CLASS_AC) == CLF_CLASS_AC, "heap corruption is never noise");
}

static void managed_noise_is_its_own_bucket(void)
{
    static clf_state s;
    unsigned int hits, a;
    for (a = 0; a < 40; a++)
        check(FC(&s, AV, 0x70000000u + a * 16, CLF_CLASS_MANAGED, 1) == CLF_LOG_NOISE, "managed noise written once per address");
    check(s.counted == 0, "and never charged to the AC limit");
}

static void last_chance_is_separate(void)
{
    static clf_state s;
    unsigned int hits, a;
    int i, logged = 0;
    for (a = 0; a < CLF_MAX_COUNTED + 5; a++)
        FC(&s, AV, 0x00700000u + a * 16, CLF_CLASS_AC, 1);
    check(s.counted == CLF_MAX_COUNTED, "first-chance limit reached");
    check(clf_last_chance(&s) == CLF_LOG_LAST_CHANCE, "the unhandled one is still written");
    for (i = 0; i < 20; i++)
        if (clf_last_chance(&s) == CLF_LOG_LAST_CHANCE) logged++;
    check(logged == CLF_MAX_LAST_CHANCE - 1, "last-chance has its own small limit");
}

static void full_table_evicts_oldest_noise(void)
{
    static clf_state s;
    unsigned int hits, a;
    clf_decision d;
    for (a = 0; a < CLF_ADDR_SLOTS; a++)
        FC(&s, AV, 0x40000000u + a * 16, CLF_CLASS_NETFX, 1000 + a);   /* noise fills it, 0x40000000 oldest */
    check(s.counted == 0, "table full of .NET Framework addresses");
    d = FC(&s, AV, 0x0055FA24u, CLF_CLASS_AC, 5000);
    check(d == CLF_LOG_COUNTED, "an AC fault is written");
    check(clf_find(&s, 0x0055FA24u) != 0, "and tracked, in the evicted slot");
    check(clf_find(&s, 0x40000000u) == 0, "the least recently written noise slot was evicted");
    check(clf_find(&s, 0x40000010u) != 0, "the next one was not");
    check(FC(&s, AV, 0x0055FA24u, CLF_CLASS_AC, 5001) == CLF_LOG_REPEAT && hits == 2, "so its repeat is deduped");
    d = FC(&s, AV, 0x41000000u, CLF_CLASS_NETFX, 6000);
    check(d == CLF_LOG_NOISE, "a new .NET Framework address is written, evicting an older one");
    check(clf_find(&s, 0x0055FA24u) != 0, "AC slots are never evicted");
}

#define FATAL_ADDR 0x00401234u

/* N contained hits at one address, then the fatal one, then
 * a terminate from a handler, so no last-chance filter runs. The fatal hit (N+1) is not due
 * a write, so only the ring's flush can put it in the log. */
static void fatal_repeat_recovered_by_flush(unsigned int n)
{
    clf_state s;
    clf_ring g;
    clf_rec out[CLF_RING];
    unsigned int hits, i, older, now = 5000;
    clf_decision d;
    int got;
    memset(&s, 0, sizeof s);
    memset(&g, 0, sizeof g);
    for (i = 0; i < n; i++) {
        d = FC(&s, AV, FATAL_ADDR, CLF_CLASS_AC, now + i);
        clf_ring_push(&g, FATAL_ADDR, AV, FATAL_ADDR, 77, now + i, hits, is_write(d));
    }
    d = FC(&s, AV, FATAL_ADDR, CLF_CLASS_AC, now + n + 1500);   /* the fatal hit, 1.5 s later */
    check(d == CLF_SKIP_REPEAT && hits == n + 1, "fatal hit N+1 is not due a write: the dedupe alone loses it");
    clf_ring_push(&g, FATAL_ADDR, AV, FATAL_ADDR, 77, now + n + 1500, hits, is_write(d));
    got = clf_ring_take(&g, out, &older);
    check(got >= 1, "the flush has records");
    check(got >= 1 && out[got - 1].addr == FATAL_ADDR && out[got - 1].hits == n + 1
          && out[got - 1].tickMs == now + n + 1500, "the flush ends with the fatal hit (N+1)");
    for (i = 0; i < (unsigned int)got; i++)
        check(!out[i].written, "a flush never repeats a record written on its own");
    check(clf_ring_take(&g, out, &older) == 0, "a second flush writes nothing");
}

static void ring_order_overflow_and_written(void)
{
    clf_ring g;
    clf_rec out[CLF_RING];
    unsigned int i, older;
    int got;
    memset(&g, 0, sizeof g);
    clf_ring_push(&g, 0x10, AV, 0, 1, 1, 1, 1);    /* written */
    clf_ring_push(&g, 0x20, AV, 0, 1, 2, 2, 0);
    clf_ring_push(&g, 0x30, AV, 0, 1, 3, 1, 1);    /* written */
    clf_ring_push(&g, 0x40, AV, 0, 1, 4, 3, 0);
    got = clf_ring_take(&g, out, &older);
    check(got == 2 && out[0].addr == 0x20 && out[1].addr == 0x40, "only unwritten records, oldest first");
    check(older == 0, "nothing lost under the ring size");

    for (i = 0; i < 40; i++) clf_ring_push(&g, 0x1000 + i, AV, 0, 1, 100 + i, 2, 0);
    got = clf_ring_take(&g, out, &older);
    check(got == CLF_RING, "40 unwritten since the last flush: the ring holds the newest 16");
    check(older == 24, "and reports the 24 it no longer holds");
    check(out[0].addr == 0x1000 + 24 && out[CLF_RING - 1].addr == 0x1000 + 39, "newest 16, oldest first");

    clf_ring_push(&g, 0x9999, AV, 0, 1, 500, 2, 0);
    got = clf_ring_take(&g, out, &older);
    check(got == 1 && out[0].addr == 0x9999 && older == 0, "after a flush, only what came since");
}

/* Review fix: Decal's handled null-ref AV at one JIT-code address about once a minute was
 * written on EVERY hit (the 30 s relog applied to noise). A day of it: 1,440 hits. */
static void noise_once_a_minute_is_not_written_every_hit(void)
{
    static clf_state s;
    unsigned int hits, i, written = 0, now = 100000u;
    for (i = 0; i < 1440; i++, now += 60000u)
        if (is_write(FC(&s, AV, 0x433620C8u, CLF_CLASS_NETFX, now))) written++;
    /* first + powers of two 2..1024 = 1 + 10 */
    check(written == 11, "1,440 once-a-minute hits of one noise address: 11 entries, not 1,440");
    check(s.counted == 0, "and none charged to the limit");
    check(s.noiseRepeats == 1440 - 11, "skipped noise repeats are counted");
    {
        static clf_state m;
        written = 0;
        for (i = 0; i < 1440; i++) if (is_write(FC(&m, AV, 0x70001000u, CLF_CLASS_MANAGED, 5000u + i * 60000u))) written++;
        check(written == 11, "managed noise is the same");
    }
}

/* A genuine (AC) address keeps its 30 s relog, but only CLF_MAX_RELOGS times. */
static void ac_relog_is_capped_per_address(void)
{
    static clf_state s;
    unsigned int hits, i, written = 0, now = 1000u;
    check(FC(&s, AV, 0x00416C86u, CLF_CLASS_AC, now) == CLF_LOG_COUNTED, "first sighting written");
    for (i = 2; i <= 1500; i++) {
        now += 31000u;
        if (is_write(FC(&s, AV, 0x00416C86u, CLF_CLASS_AC, now))) written++;
    }
    {
        unsigned int pow2 = 0, v;
        for (v = 2; v <= 1500; v <<= 1) pow2++;          /* 2..1024 = 10 */
        check(written == pow2 + CLF_MAX_RELOGS, "1,500 hits 31 s apart: the power-of-two ones + CLF_MAX_RELOGS time relogs");
    }
    check(s.relogCapped > 0, "the skipped relogs are counted");
    check(clf_find(&s, 0x00416C86u)->relogs == CLF_MAX_RELOGS, "the slot used its relog budget");
}

/* A non-AV code at a known address is written, within the same per-address cap. */
static void non_av_relog_is_capped(void)
{
    static clf_state s;
    unsigned int hits, i, written = 0;
    FC(&s, AV, 0x00500000u, CLF_CLASS_AC, 100);
    for (i = 0; i < 200; i++)
        if (is_write(FC(&s, CLF_CODE_HEAP_CORRUPTION, 0x00500000u, CLF_CLASS_AC, 200 + i))) written++;
    /* hits 2..201: powers of two 2,4,..,128 = 7, plus the capped relogs */
    check(written == 7 + CLF_MAX_RELOGS, "non-AV repeats: powers of two + CLF_MAX_RELOGS, not every hit");
}

/* The byte budget: ordinary entries stop after one marker; fatal ones get a reserve. */
static void byte_budget(void)
{
    static clf_state s;
    int i;
    check(clf_bytes_gate(&s, 0) == CLF_BYTES_OK, "an empty log accepts an entry");
    s.logBytes = CLF_MAX_LOG_BYTES - 1;
    check(clf_bytes_gate(&s, 0) == CLF_BYTES_OK, "one byte under the budget still accepts");
    s.logBytes = CLF_MAX_LOG_BYTES;
    check(clf_bytes_gate(&s, 0) == CLF_BYTES_MARK, "at the budget: the marker, once");
    for (i = 0; i < 5; i++)
        check(clf_bytes_gate(&s, 0) == CLF_BYTES_DENY, "then ordinary entries are dropped");
    check(s.byteDenied == 6, "and counted");
    check(clf_bytes_gate(&s, 1) == CLF_BYTES_OK, "a fatal entry still fits in the reserve");
    s.logBytes = CLF_MAX_LOG_BYTES + CLF_LOG_RESERVE_BYTES;
    check(clf_bytes_gate(&s, 1) == CLF_BYTES_DENY, "and not past the reserve");
    {
        static clf_state t;   /* a log that starts over the budget (shared file already big) */
        t.logBytes = CLF_MAX_LOG_BYTES + 10;
        check(clf_bytes_gate(&t, 1) == CLF_BYTES_OK, "a fatal entry fits in the reserve of a log that opened over budget");
        check(clf_bytes_gate(&t, 0) == CLF_BYTES_MARK, "its first ordinary entry gets the marker");
    }
}

int main(void)
{
    noise_once_a_minute_is_not_written_every_hit();
    ac_relog_is_capped_per_address();
    non_av_relog_is_capped();
    byte_budget();
    fatal_repeat_recovered_by_flush(5);
    fatal_repeat_recovered_by_flush(100);
    fatal_repeat_recovered_by_flush(1000);
    ring_order_overflow_and_written();
    netfx_noise_never_spends_the_limit();
    known_address_going_fatal_is_written();
    ac_faults_limited_per_distinct_address();
    codes();
    classification();
    managed_noise_is_its_own_bucket();
    last_chance_is_separate();
    full_table_evicts_oldest_noise();
    printf("%d check(s), %d failed.\n", g_checks, g_failed);
    return g_failed == 0 ? 0 : 1;
}
