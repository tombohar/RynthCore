"""Check that every Dear ImGui function the engine calls exists in our cimgui.dll.

ImGui.NET binds native functions by name and lazily, so a call to a function
the DLL doesn't export (our build compiles out obsolete APIs, e.g.
GetContentRegionMax in 1.91) compiles fine and only fails - on AC's render
thread - the first time that line runs. Run after adding ImGui code:

    python tools/check_imgui_calls.py

Exit code 1 lists the calls with no matching export.

It also flags ImGui.Text / TextColored / TextWrapped / TextDisabled /
BulletText / SetTooltip / SetItemTooltip / LabelText(value) calls anywhere in
the engine whose text isn't a plain literal: ImGui.NET passes that text to
native ImGui as a printf format, so a '%' in it reads missing arguments. Such
text goes through SafeImGui instead. To check another tree (a RynthSuite
checkout's Plugins folder) for those calls only:

    python tools/check_imgui_calls.py C:/path/to/RynthSuite/Plugins
"""
import re
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / "src" / "RynthCore.Engine" / "Native" / "cimgui.dll"
ENGINE = ROOT / "src" / "RynthCore.Engine"
SOURCES = ENGINE / "ImGui"
# Old diagnostic shell, FORCE-gated and never run: not held to this check.
SKIP = {"RynthCoreShell.cs"}

# Wrapper-object calls: variable/field name -> cimgui prefix.
MEMBER_PREFIX = {
    "dl": "ImDrawList_",
    "clipper": "ImGuiListClipper_",
    "io.Fonts": "ImFontAtlas_",
    "style": "ImGuiStyle_",
}


def pe_exports(path: Path) -> set[str]:
    data = path.read_bytes()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", data, opt)[0]
    dd = opt + (96 if magic == 0x10B else 112)
    exp_rva = struct.unpack_from("<I", data, dd)[0]
    sections = []
    sec = opt + struct.unpack_from("<H", data, pe + 20)[0]
    for i in range(nsec):
        vsize, va, raw_size, raw_ptr = struct.unpack_from("<IIII", data, sec + i * 40 + 8)
        sections.append((va, max(vsize, raw_size), raw_ptr))

    def off(rva: int) -> int:
        for va, size, raw in sections:
            if va <= rva < va + size:
                return rva - va + raw
        raise ValueError(hex(rva))

    e = off(exp_rva)
    count, names_rva = struct.unpack_from("<I", data, e + 24)[0], struct.unpack_from("<I", data, e + 32)[0]
    names = set()
    for i in range(count):
        n = off(struct.unpack_from("<I", data, off(names_rva) + i * 4)[0])
        names.add(data[n:data.index(b"\0", n)].decode())
    return names


# --- Text drawn as a printf format -------------------------------------------
# ImGui.NET hands the string of these calls to native ImGui as a printf format
# with no arguments: a '%' in a chat line, name, server reply or setting reads
# arguments that were never passed (garbage, or a crash on %s / %n). Text that
# isn't a literal goes through SafeImGui (engine: ImGui/SafeImGui.cs, RynthSuite:
# Plugins/Shared/SafeImGui.cs), which doubles every '%'. The number is the
# index of the format argument.
FORMAT_TEXT_CALLS = {
    "Text": 0, "TextColored": 1, "TextWrapped": 0, "TextDisabled": 0,
    "BulletText": 0, "SetTooltip": 0, "SetItemTooltip": 0, "LabelText": 1,
}
FORMAT_CALL_RE = re.compile(
    r"(?<![\w.])(?:ImGuiNET\.)?ImGui\.(" + "|".join(FORMAT_TEXT_CALLS) + r")\s*\(")
STRING_START_RE = re.compile(r'(\$*)(@?)(\$*)"')
# The wrappers themselves pass their (escaped) text on.
FORMAT_SKIP = {"SafeImGui.cs"}


def skip_token(s: str, i: int):
    """End of the comment, string or char literal starting at i, else None."""
    if s.startswith("//", i):
        j = s.find("\n", i)
        return len(s) if j < 0 else j
    if s.startswith("/*", i):
        j = s.find("*/", i + 2)
        return len(s) if j < 0 else j + 2
    c = s[i]
    if c == "'":
        j = i + 1
        while j < len(s) and s[j] != "'":
            j += 2 if s[j] == "\\" else 1
        return j + 1
    if c not in '"$@':
        return None
    m = STRING_START_RE.match(s, i)
    if not m:
        return None
    dollars = len(m.group(1)) + len(m.group(3))
    verbatim = bool(m.group(2))
    q = m.end() - 1
    n = 0
    while q + n < len(s) and s[q + n] == '"':
        n += 1
    if n == 2:                                   # ""
        return q + 2
    if n >= 3:                                   # raw string literal
        j = q + n
        close = '"' * n
        while j < len(s) and not s.startswith(close, j):
            if dollars and s.startswith("{" * dollars, j):
                j = skip_code(s, j + dollars, "}") + 1
            else:
                j += 1
        return j + n
    j = q + 1
    while j < len(s):
        ch = s[j]
        if ch == "\\" and not verbatim:
            j += 2
        elif ch == '"':
            if verbatim and s.startswith('""', j):
                j += 2
            else:
                return j + 1
        elif dollars and ch == "{":
            if s.startswith("{{", j):
                j += 2
            else:
                j = skip_code(s, j + 1, "}") + 1
        else:
            j += 1
    return j


def skip_code(s: str, i: int, closer: str) -> int:
    """Index of the closer that ends the code starting at i (nesting, strings and comments skipped)."""
    depth = 0
    j = i
    while j < len(s):
        end = skip_token(s, j)
        if end is not None:
            j = end
            continue
        ch = s[j]
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            if depth == 0 and ch == closer:
                return j
            depth -= 1
        j += 1
    return j


def call_args(s: str, open_paren: int):
    """[(start, end)] of each top-level argument of the call whose '(' is at open_paren."""
    args = []
    depth = 0
    start = open_paren + 1
    j = start
    while j < len(s):
        end = skip_token(s, j)
        if end is not None:
            j = end
            continue
        ch = s[j]
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            if depth == 0:
                args.append((start, j))
                return args, j
            depth -= 1
        elif ch == "," and depth == 0:
            args.append((start, j))
            start = j + 1
        j += 1
    return args, j


def literal_kind(arg: str) -> str:
    """'literal' for plain string literals (joined with '+' at most), 'percent' for such
    literals holding a lone '%', else 'expression'."""
    j = 0
    pieces = []
    while j < len(arg):
        ch = arg[j]
        if ch.isspace() or ch == "+":
            j += 1
            continue
        end = skip_token(arg, j)
        if end is None:
            return "expression"
        piece = arg[j:end]
        if not (piece.startswith("//") or piece.startswith("/*")):
            if piece[0] not in '"@$' or "$" in piece[:piece.index('"')]:
                return "expression"
            pieces.append(piece)
        j = end
    if not pieces:
        return "expression"
    return "percent" if any("%" in p.replace("%%", "") for p in pieces) else "literal"


def format_text_calls(text: str):
    """Every format-text ImGui call in C# source: (match, arg_start, arg_end, kind).
    Calls inside comments and strings (e.g. Lua script text) are skipped."""
    code = []
    j = 0
    while j < len(text):
        end = skip_token(text, j)
        if end is None:
            code.append(text[j])
            j += 1
        else:
            code.append("".join(c if c == "\n" else " " for c in text[j:end]))
            j = end
    masked = "".join(code)
    for m in FORMAT_CALL_RE.finditer(masked):
        args, _ = call_args(text, m.end() - 1)
        index = FORMAT_TEXT_CALLS[m.group(1)]
        if index >= len(args):
            yield m, m.end(), m.end(), "expression"
            continue
        a, b = args[index]
        yield m, a, b, literal_kind(text[a:b])


def unsafe_format_text(roots) -> list[str]:
    found = []
    for root in roots:
        for f in sorted(Path(root).rglob("*.cs")):
            if f.name in FORMAT_SKIP or {"bin", "obj"} & set(f.parts):
                continue
            text = f.read_text(encoding="utf-8-sig")
            for m, a, b, kind in format_text_calls(text):
                if kind == "literal":
                    continue
                line = text.count("\n", 0, m.start()) + 1
                what = "a literal with a lone '%'" if kind == "percent" else "text that isn't a literal"
                found.append(f"{f}:{line}: ImGui.{m.group(1)} with {what}: {' '.join(text[a:b].split())[:80]}")
    return found


def main() -> int:
    # python tools/check_imgui_calls.py DIR...  checks only the format text under DIR
    # (e.g. a RynthSuite checkout's Plugins folder).
    if len(sys.argv) > 1:
        unsafe = unsafe_format_text(sys.argv[1:])
        if unsafe:
            print("ImGui text calls that draw non-literal text as a printf format (use SafeImGui):")
            print("\n".join("  " + u for u in unsafe))
            return 1
        print("OK: no ImGui text call draws non-literal text as a format.")
        return 0

    exports = pe_exports(DLL)

    def found(prefix: str, name: str) -> bool:
        full = prefix + name
        return full in exports or any(x.startswith(full + "_") for x in exports)

    missing = []
    for f in sorted(SOURCES.rglob("*.cs")):
        if f.name in SKIP:
            continue
        text = f.read_text(encoding="utf-8")
        for m in re.finditer(r"(?<!\w)ImGui(?:NET\.ImGui)?\.(\w+)\(", text):
            if not found("ig", m.group(1)):
                missing.append(f"{f.relative_to(ROOT)}:{text.count(chr(10), 0, m.start()) + 1}: ImGui.{m.group(1)}")
        # Direct native calls name the exact export (overload suffix included).
        for m in re.finditer(r"\bImGuiNative\.(\w+)\(", text):
            if m.group(1) not in exports:
                missing.append(f"{f.relative_to(ROOT)}:{text.count(chr(10), 0, m.start()) + 1}: ImGuiNative.{m.group(1)}")
        for m in re.finditer(r"\b(dl|clipper|io\.Fonts|style)\.(\w+)\(", text):
            if not found(MEMBER_PREFIX[m.group(1)], m.group(2)):
                missing.append(f"{f.relative_to(ROOT)}:{text.count(chr(10), 0, m.start()) + 1}: {m.group(1)}.{m.group(2)}")

    unsafe = unsafe_format_text([ENGINE])

    if missing:
        print("ImGui calls with no export in cimgui.dll:")
        print("\n".join("  " + m for m in missing))
    if unsafe:
        print("ImGui text calls that draw non-literal text as a printf format (use SafeImGui):")
        print("\n".join("  " + u for u in unsafe))
    if missing or unsafe:
        return 1
    print(f"OK: every ImGui call resolves ({len(exports)} exports); no text call draws non-literal text as a format.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
