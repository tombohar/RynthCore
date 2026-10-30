"""Checks src/RynthCore.Engine/ImGui/PhosphorIcons.cs against the Phosphor font.

  - every constant's code point is in the font's cmap;
  - its name matches the font's own name for that code point (the font's
    ligature table spells each icon's kebab-case name; single-letter names
    such as "x" have no ligature and are only checked by code point);
  - Baked holds every constant the ImGui code uses (PhosphorIcons.Name), so
    no icon draws as nothing because it isn't in the atlas.

Needs fontTools (pip install fonttools). Exit code 1 on any problem.
Usage: python tools/check_phosphor_icons.py
"""
import os
import re
import sys

from fontTools.ttLib import TTFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ENGINE = os.path.join(ROOT, 'src', 'RynthCore.Engine')
FONT = os.path.join(ENGINE, 'Assets', 'Fonts', 'Phosphor.ttf')
ICONS = os.path.join(ENGINE, 'ImGui', 'PhosphorIcons.cs')
IMGUI = os.path.join(ENGINE, 'ImGui')


def ligature_names(font):
    """{kebab-name: code point} from the font's GSUB ligatures."""
    cmap = font.getBestCmap()
    by_glyph = {}
    for cp, glyph in cmap.items():
        by_glyph.setdefault(glyph, cp)
    letters = {g: chr(cp) for g, cp in by_glyph.items() if cp < 0x80}
    names = {}
    for lookup in font['GSUB'].table.LookupList.Lookup:
        for sub in lookup.SubTable:
            if sub.LookupType == 7:
                sub = sub.ExtSubTable
            for first, ligs in getattr(sub, 'ligatures', {}).items():
                for lig in ligs:
                    name = ''.join(letters.get(g, '?') for g in [first] + list(lig.Component))
                    if lig.LigGlyph in by_glyph:
                        names[name] = by_glyph[lig.LigGlyph]
    return names


def kebab(pascal):
    return re.sub(r'(?<!^)(?=[A-Z])', '-', pascal).lower()


def main():
    font = TTFont(FONT)
    cmap = font.getBestCmap()
    names = ligature_names(font)
    src = open(ICONS, encoding='utf-8-sig').read()
    consts = {m.group(1): int(m.group(2), 16)
              for m in re.finditer(r'public const string (\w+) = "\\u([0-9A-Fa-f]{4})";', src)}
    baked_expr = re.search(r'public const string Baked =(.*?);', src, re.S).group(1)
    baked = set(re.findall(r'\b([A-Z]\w*)\b', baked_expr))

    problems = []
    for name, cp in sorted(consts.items()):
        if cp not in cmap:
            problems.append(f'{name}: U+{cp:04X} is not in the font')
        k = kebab(name)
        if k in names and names[k] != cp:
            problems.append(f'{name}: U+{cp:04X}, but the font has {k} at U+{names[k]:04X}')
        elif k not in names:
            print(f'note: {name} ({k}) has no ligature name in the font; checked by code point only')
    for name in sorted(baked - consts.keys()):
        problems.append(f'Baked names {name}, which is not a constant')

    used = set()
    for dirpath, _, files in os.walk(IMGUI):
        for f in files:
            if f.endswith('.cs') and f != 'PhosphorIcons.cs':
                text = open(os.path.join(dirpath, f), encoding='utf-8-sig').read()
                used |= set(re.findall(r'PhosphorIcons\.(\w+)', text))
    used -= {'Baked', 'DrawCentered'}
    for name in sorted(used - consts.keys()):
        problems.append(f'PhosphorIcons.{name} is used but not declared')
    for name in sorted((used & consts.keys()) - baked):
        problems.append(f'PhosphorIcons.{name} is used but not in Baked (it would draw as nothing)')
    unused = sorted(baked - used)
    if unused:
        print('note: in Baked but not used by the ImGui code: ' + ', '.join(unused))

    print(f'{len(consts)} constants, {len(baked)} baked, {len(used)} used.')
    for p in problems:
        print('PROBLEM: ' + p)
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
