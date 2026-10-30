"""Step 1 of the creature size table (see README.md).

Reads the ACE world database dump and writes, to the output folder:
  creatures.json  {weenie class id: {name, wtype, setup, scale}} for every creature-like weenie
                  (creature, admin, vendor, cow, AI) that has a Setup (model) property
  setups.json     the distinct setup ids, for SetupDims

usage: python parse_world.py <ACE-World-Database.sql> [<more weenie .sql files>] -o <out dir>

The extra files are weenie exports in the same INSERT format (the Aelrynth custom weenies).
"""
import argparse
import json
import os
import re

ap = argparse.ArgumentParser()
ap.add_argument("sql", nargs="+")
ap.add_argument("-o", "--out", default=".")
args = ap.parse_args()

# weenie rows: (class_Id,'class_Name',type,'last_Modified')
WEENIE_ROW = re.compile(r"\((\d+),'((?:[^'\\]|\\.|'')*)',(\d+),'[^']*'\)")
PLAIN_ROW = re.compile(r"\(([^()]*)\)")

weenies, setup, scale = {}, {}, {}
for path in args.sql:
    text = open(path, encoding="utf-8", errors="replace").read()

    def inserts(table):
        for m in re.finditer(r"INSERT INTO `%s` VALUES (.*?);\n" % table, text, re.S):
            yield m.group(1)

    for body in inserts("weenie"):
        for r in WEENIE_ROW.finditer(body):
            weenies[int(r.group(1))] = {"name": r.group(2), "wtype": int(r.group(3))}
    # property rows: (id, object_Id, type, value). Setup = DataId property 1, DefaultScale = float property 39.
    for body in inserts("weenie_properties_d_i_d"):
        for r in PLAIN_ROW.finditer(body):
            p = r.group(1).split(",")
            if len(p) == 4 and p[2] == "1":
                setup[int(p[1])] = int(p[3])
    for body in inserts("weenie_properties_float"):
        for r in PLAIN_ROW.finditer(body):
            p = r.group(1).split(",")
            if len(p) == 4 and p[2] == "39":
                scale[int(p[1])] = float(p[3])

out = {}
for w, info in weenies.items():
    if info["wtype"] not in (10, 11, 12, 15, 16):   # creature, admin, vendor, cow, AI
        continue
    if w not in setup:
        continue
    out[w] = {"name": info["name"], "wtype": info["wtype"], "setup": setup[w], "scale": scale.get(w, 1.0)}

os.makedirs(args.out, exist_ok=True)
json.dump(out, open(os.path.join(args.out, "creatures.json"), "w"))
json.dump(sorted(set(v["setup"] for v in out.values())), open(os.path.join(args.out, "setups.json"), "w"))
print(len(weenies), "weenies;", len(out), "creature-like with a setup;",
      len(set(v["setup"] for v in out.values())), "distinct setups;",
      sum(1 for v in out.values() if v["scale"] != 1.0), "with a default scale")
