/*
 * DumpArgsTests.c - offline checks for DumpArgs.h (RC_DumpProcess's command line, the hang
 * dump written from rundll32). Build and run with Run-DumpArgsTests.ps1 (x86 MSVC). Exit 0 = pass.
 */
#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include "../DumpArgs.h"

static int g_checks, g_failed;

static void check(int ok, const char* what)
{
    g_checks++;
    if (ok) return;
    g_failed++;
    printf("[FAIL] %s\n", what);
}

int main(void)
{
    unsigned long pid;
    wchar_t path[260];

    /* What HangDumpCommand.Arguments leaves for the entry (rundll32 passes the text after the entry name). */
    check(ParseDumpArgs(L"17132 C:\\Games\\RynthCore\\Logs\\dumps\\hang_17132_20261031_211714.dmp", &pid, path, 260)
          && pid == 17132 && wcscmp(path, L"C:\\Games\\RynthCore\\Logs\\dumps\\hang_17132_20261031_211714.dmp") == 0,
          "pid and path");
    check(ParseDumpArgs(L"  42   C:\\Users\\Some One's PC\\a b.dmp  ", &pid, path, 260)
          && pid == 42 && wcscmp(path, L"C:\\Users\\Some One's PC\\a b.dmp") == 0,
          "spaces and an apostrophe in the path are kept, the ends trimmed");
    check(ParseDumpArgs(L"7 \"C:\\x y\\z.dmp\"", &pid, path, 260) && pid == 7 && wcscmp(path, L"C:\\x y\\z.dmp") == 0,
          "a quoted path loses its quotes");
    check(!ParseDumpArgs(L"", &pid, path, 260), "empty refused");
    check(!ParseDumpArgs(NULL, &pid, path, 260), "null refused");
    check(!ParseDumpArgs(L"123", &pid, path, 260), "no path refused");
    check(!ParseDumpArgs(L"0 C:\\a.dmp", &pid, path, 260), "pid 0 refused");
    check(!ParseDumpArgs(L"x C:\\a.dmp", &pid, path, 260), "no pid refused");
    check(!ParseDumpArgs(L"12x C:\\a.dmp", &pid, path, 260), "a pid run into text refused");
    check(!ParseDumpArgs(L"99999999999 C:\\a.dmp", &pid, path, 260), "an overflowing pid refused");
    check(ParseDumpArgs(L"4294967295 C:\\a.dmp", &pid, path, 260) && pid == 4294967295UL, "the largest pid");
    check(!ParseDumpArgs(L"5 C:\\abcdefgh.dmp", &pid, path, 8), "a path longer than the buffer refused, not cut");
    check(ParseDumpArgs(L"5 C:\\a.dmp", &pid, path, 9) && wcscmp(path, L"C:\\a.dmp") == 0, "a path that just fits");

    printf("%d check(s), %d failed.\n", g_checks, g_failed);
    return g_failed == 0 ? 0 : 1;
}
