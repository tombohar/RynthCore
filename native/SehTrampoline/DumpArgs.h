/*
 * DumpArgs.h - the command line of RC_DumpProcess (SehTrampoline.c): "<pid> <dump path>", the
 * path being the rest of the line (spaces kept, trailing spaces and a closing quote dropped).
 * The engine writes it (src\RynthCore.Engine\HangDumpCommand.cs). Pure: tests\DumpArgsTests.c.
 */
#pragma once
#include <wchar.h>

/* 1 when `cmd` holds a pid above 0 and a non-empty path that fits `cap` characters (with its 0). */
static int ParseDumpArgs(const wchar_t* cmd, unsigned long* pid, wchar_t* path, int cap)
{
    unsigned long p = 0;
    int n = 0;
    if (!cmd || !pid || !path || cap < 2) return 0;
    path[0] = 0;
    while (*cmd == L' ' || *cmd == L'\t') cmd++;
    if (*cmd < L'0' || *cmd > L'9') return 0;
    while (*cmd >= L'0' && *cmd <= L'9') {
        unsigned long d = (unsigned long)(*cmd - L'0');
        if (p > (4294967295UL - d) / 10) return 0;   /* would overflow */
        p = p * 10 + d;
        cmd++;
    }
    if (*cmd != L' ' && *cmd != L'\t') return 0;
    while (*cmd == L' ' || *cmd == L'\t') cmd++;
    if (*cmd == L'"') cmd++;
    while (*cmd && *cmd != L'"') {
        if (n >= cap - 1) return 0;      /* too long: refuse rather than cut the name */
        path[n++] = *cmd++;
    }
    while (n > 0 && (path[n - 1] == L' ' || path[n - 1] == L'\t')) n--;
    path[n] = 0;
    *pid = p;
    return p != 0 && n > 0;
}
