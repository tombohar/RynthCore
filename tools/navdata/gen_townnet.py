"""Builds townnet.json: the Town Network's portals, for RynthNav's route planner.

The Town Network is the indoor hub (landblock 0x0007 on retail data) with a
"Portal to Town Network" in many towns and, inside, an exit portal to each town.
This script writes what the world database knows about it:

  arrivals  each place an entry portal lands you, inside the network
  entries   every placed portal (outdoors, in a building, or in a dungeon) whose
            destination is inside the network: where it stands on the map, the
            nearest town, and which arrival it lands at
  exits     every placed portal inside the network: where it stands, where it
            goes (cell, position, map coordinates, nearest town)
  npcs      the vendors and other creatures standing inside, as obstacles

Every portal carries the restrictions the server checks before it lets a
character through (ACE Portal.CheckUseRequirements): minLevel, maxLevel, quest
(the QuestRestriction flag name), closed (PortalBitmask 0: nobody may use it),
noPk / noPkLite / noNpk / onlyOlthoi / noOlthoi / noVitae / noNewAccounts (the
PortalBitmask bits) and accountRequirements (a SubscriptionStatus; ACE gives
every player 1, so 1 restricts nobody).

The walking paths inside (from every arrival to every exit portal) need the
dats; RynthSuite's Tools/RynthNav.TownNet adds them to the same file:
  dotnet run -c Release --project <RynthSuite>/Tools/RynthNav.TownNet -- build --in townnet.json
Build-TownNet.ps1 (next to this script) runs both steps.

Usage (same options as gen_locations.py; SELECT only, the password stays on the server):
  python tools/navdata/gen_townnet.py --out <dir> [--dump <dir> | --from-dump <dir>]
  --lb 0007,...   the network's landblocks, instead of finding them from the
                  "Town Network" portals' destinations

Only our own world database is read; no GoArrow or Crossroads of Dereth data.
"""

import argparse
import datetime
import hashlib
import json
import os
import re
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gen_locations as g  # noqa: E402

FORMAT_VERSION = 1

# Portal weenies named like this lead into the network; their destinations name its landblocks.
TOWN_NETWORK_NAME = re.compile(r'town\s*network', re.I)

# A portal counts as "in" (or going to) the nearest town within this many map units (0.1 = 24 yd).
TOWN_RADIUS = 3.0

# PortalBitmask (ACE.Entity.Enum.PortalBitmask) bits a planner must respect.
PORTAL_BITS = [(0x02, 'noPk'), (0x04, 'noPkLite'), (0x08, 'noNpk'), (0x40, 'onlyOlthoi'),
               (0x80, 'noOlthoi'), (0x100, 'noVitae'), (0x200, 'noNewAccounts')]

_PORTAL_FIELDS = """
  li.guid, li.weenie_Class_Id, li.obj_Cell_Id, li.origin_X, li.origin_Y, li.origin_Z,
  li.angles_W, li.angles_X, li.angles_Y, li.angles_Z,
  (select s.value from weenie_properties_string s where s.object_Id=li.weenie_Class_Id and s.type=1 limit 1),
  pp.obj_Cell_Id, pp.origin_X, pp.origin_Y, pp.origin_Z,
  (select i.value from weenie_properties_int i where i.object_Id=li.weenie_Class_Id and i.type=86 limit 1),
  (select i.value from weenie_properties_int i where i.object_Id=li.weenie_Class_Id and i.type=87 limit 1),
  (select s.value from weenie_properties_string s where s.object_Id=li.weenie_Class_Id and s.type=37 limit 1),
  (select i.value from weenie_properties_int i where i.object_Id=li.weenie_Class_Id and i.type=111 limit 1),
  (select i.value from weenie_properties_int i where i.object_Id=li.weenie_Class_Id and i.type=26 limit 1),
  (select b.value+0 from weenie_properties_bool b where b.object_Id=li.weenie_Class_Id and b.type=43 limit 1),
  (select d.value from weenie_properties_d_i_d d where d.object_Id=li.weenie_Class_Id and d.type=1 limit 1),
  (select f.value from weenie_properties_float f where f.object_Id=li.weenie_Class_Id and f.type=39 limit 1)
from landblock_instance li
join weenie w on w.class_Id=li.weenie_Class_Id and w.type=7
left join weenie_properties_position pp on pp.object_Id=li.weenie_Class_Id and pp.position_Type=2"""

QUERIES = {
    # Portal weenies whose name says Town Network, and where they go.
    'tn_weenies': """
select s.object_Id, s.value, pp.obj_Cell_Id
from weenie_properties_string s
join weenie w on w.class_Id=s.object_Id and w.type=7
join weenie_properties_position pp on pp.object_Id=s.object_Id and pp.position_Type=2
where s.type=1 and s.value like '%Town Network%'
order by s.object_Id""",
    # Every placed portal with a destination: filtered to the network here, so the query
    # does not depend on the landblock list (and one dump serves any --lb).
    'tn_portals': 'select' + _PORTAL_FIELDS + """
where pp.obj_Cell_Id is not null
order by li.guid""",
    # Vendors and other creatures standing in the network (obstacles for the walks).
    'tn_npcs': """
select li.guid, li.weenie_Class_Id, w.type, li.obj_Cell_Id, li.origin_X, li.origin_Y, li.origin_Z,
  (select s.value from weenie_properties_string s where s.object_Id=li.weenie_Class_Id and s.type=1 limit 1),
  (select d.value from weenie_properties_d_i_d d where d.object_Id=li.weenie_Class_Id and d.type=1 limit 1)
from landblock_instance li
join weenie w on w.class_Id=li.weenie_Class_Id and w.type in (10,12)
where (li.obj_Cell_Id & 65535) >= 256
order by li.guid""",
    'poi': g.QUERIES['poi'],
    'surface': g.QUERIES['surface'],
}


def fetch(args):
    raw = {}
    for name, sql in QUERIES.items():
        if args.from_dump:
            with open(os.path.join(args.from_dump, name + '.tsv'), encoding='utf-8') as f:
                raw[name] = f.read()
        else:
            raw[name] = g.run_query(args.host, args.db, sql)
        if args.dump:
            os.makedirs(args.dump, exist_ok=True)
            with open(os.path.join(args.dump, name + '.tsv'), 'w', encoding='utf-8', newline='\n') as f:
                f.write(raw[name])
    return {k: g.parse_rows(v) for k, v in raw.items()}


def r3(v):
    return round(v, 3)


def lb_of(cell):
    return (cell >> 16) & 0xFFFF


def restrictions(minlvl, maxlvl, quest, bitmask, acct, advocate):
    """The fields a planner checks before it routes a character through a portal."""
    out = {}
    if minlvl is not None and int(minlvl) > 0:
        out['minLevel'] = int(minlvl)
    if maxlvl is not None and int(maxlvl) > 0:
        out['maxLevel'] = int(maxlvl)
    if quest:
        out['quest'] = quest.strip()
    if bitmask is not None:
        b = int(bitmask)
        if b == 0:
            out['closed'] = True
        for bit, name in PORTAL_BITS:
            if b & bit:
                out[name] = True
    if acct is not None and int(acct) > 0:
        out['accountRequirements'] = int(acct)
    if advocate == '1':
        out['advocateOnly'] = True
    return out


def build(rows, lbs_override=None):
    world = g.World([(int(a), float(b), float(c), int(d)) for a, b, c, d in rows['surface']])

    # The network's landblocks: where the "Town Network" portals lead.
    named = [(int(w), n, int(c)) for w, n, c in rows['tn_weenies'] if TOWN_NETWORK_NAME.search(n or '')]
    found = sorted({lb_of(c) for _, _, c in named})
    lbs = sorted(lbs_override) if lbs_override else found

    # Towns (as gen_locations names them), for "which town is this portal in / does it go to".
    towns = []
    by_wcid = defaultdict(list)
    for name, wcid, cell, x, y, z in rows['poi']:
        if cell is not None:
            by_wcid[int(wcid)].append((name, int(cell), float(x), float(y), float(z)))
    for _wcid, names in by_wcid.items():
        name, cell, x, y, z = max(names, key=lambda n: (n[0].count(' '), len(n[0])))
        if world.place(cell, z) in ('outdoor', 'building'):
            ns, ew = g.cell_to_map(cell, x, y)
            towns.append((name, ns, ew))

    def nearest_town(ns, ew):
        if not towns:
            return None
        name, tns, tew = min(towns, key=lambda t: (t[1] - ns) ** 2 + (t[2] - ew) ** 2)
        dist = ((tns - ns) ** 2 + (tew - ew) ** 2) ** 0.5
        return {'name': name, 'dist': round(dist, 2)} if dist <= TOWN_RADIUS else None

    def position(cell, x, y, z):
        place = world.place(cell, z)
        out = {'cell': '%08X' % cell, 'x': r3(x), 'y': r3(y), 'z': r3(z), 'place': place}
        if place != 'dungeon':
            ns, ew = g.cell_to_map(cell, x, y)
            out['ns'], out['ew'] = round(ns, 2), round(ew, 2)
        return out

    entries, exits = [], []
    arrivals = {}          # (cell, x, y, z) -> arrival dict
    seen_entries = {}
    for (guid, wcid, cell, x, y, z, aw, ax, ay, az, name, dcell, dx, dy, dz,
         minlvl, maxlvl, quest, bitmask, acct, advocate, setup, scale) in rows['tn_portals']:
        cell, dcell = int(cell), int(dcell)
        x, y, z, dx, dy, dz = float(x), float(y), float(z), float(dx), float(dy), float(dz)
        name = (name or 'Portal').strip()
        rest = restrictions(minlvl, maxlvl, quest, bitmask, acct, advocate)
        inside, into = lb_of(cell) in lbs, lb_of(dcell) in lbs
        if inside:
            dest = position(dcell, dx, dy, dz)
            town = nearest_town(dest['ns'], dest['ew']) if 'ns' in dest else None
            if town:
                dest['town'] = town
            ex = {'guid': '%08X' % int(guid), 'wcid': int(wcid), 'name': name, 'label': g.dungeon_name(name),
                  'cell': '%08X' % cell, 'x': r3(x), 'y': r3(y), 'z': r3(z),
                  'heading': heading(aw, az), 'dest': dest, 'into': into}
            if setup is not None:
                ex['setup'] = '%08X' % int(setup)
            if scale is not None:
                ex['scale'] = float(scale)
            ex.update(rest)
            exits.append(ex)
        elif into:
            key = (dcell, r3(dx), r3(dy), r3(dz))
            if key not in arrivals:
                arrivals[key] = {'cell': '%08X' % dcell, 'x': r3(dx), 'y': r3(dy), 'z': r3(dz)}
            pos = position(cell, x, y, z)
            dedupe = (int(wcid), pos['cell'], pos['x'], pos['y'], pos['z'])
            if dedupe in seen_entries:      # two placements of one portal at one spot
                seen_entries[dedupe]['guids'].append('%08X' % int(guid))
                continue
            en = {'guid': '%08X' % int(guid), 'guids': ['%08X' % int(guid)], 'wcid': int(wcid), 'name': name, **pos,
                  'arrivalKey': key}
            town = nearest_town(pos['ns'], pos['ew']) if 'ns' in pos else None
            if town:
                en['town'] = town
            en.update(rest)
            seen_entries[dedupe] = en
            entries.append(en)

    # Arrival ids in a stable order (by cell, then position).
    arr_list = sorted(arrivals.items(), key=lambda kv: kv[0])
    arr_ids = {}
    out_arrivals = []
    for i, (key, a) in enumerate(arr_list, 1):
        a = dict(id='A%d' % i, **a)
        a['entries'] = sum(1 for e in entries if e['arrivalKey'] == key)
        arr_ids[key] = a['id']
        out_arrivals.append(a)
    for e in entries:
        e['arrival'] = arr_ids[e.pop('arrivalKey')]
        if len(e['guids']) == 1:
            del e['guids']
    entries.sort(key=lambda e: (e['name'].lower(), e['cell'], e['x'], e['y']))
    exits.sort(key=lambda e: (e['cell'], e['name'].lower()))

    npcs = []
    for guid, wcid, wtype, cell, x, y, z, name, setup in rows['tn_npcs']:
        cell = int(cell)
        if lb_of(cell) not in lbs:
            continue
        n = {'guid': '%08X' % int(guid), 'wcid': int(wcid), 'name': (name or '').strip(), 'cell': '%08X' % cell,
             'x': r3(float(x)), 'y': r3(float(y)), 'z': r3(float(z))}
        if setup is not None:
            n['setup'] = '%08X' % int(setup)
        npcs.append(n)

    notes = []
    if found != lbs:
        notes.append('landblocks given with --lb (%s); the Town Network portals lead to %s'
                     % (', '.join('%04X' % l for l in lbs), ', '.join('%04X' % l for l in found) or 'none'))
    for a in out_arrivals:
        if a['entries'] == 0:
            notes.append('arrival %s has no entry portal' % a['id'])
    return {'landblocks': ['%04X' % l for l in lbs], 'arrivals': out_arrivals, 'entries': entries,
            'exits': exits, 'npcs': npcs, 'notes': notes, 'townNetworkWeenies': [w for w, _, _ in named]}


def heading(w, z):
    """Compass heading (degrees, 0 = north, clockwise) of a yaw-only rotation, or None."""
    if w is None or z is None:
        return None
    import math
    yaw = 2.0 * math.atan2(float(z), float(w))      # counter-clockwise from +y (north) in AC
    return round((-math.degrees(yaw)) % 360.0, 1)


def set_id(text):
    """The set id: SHA-256 of the file text without its "setId" and "generated" lines (and without
    carriage returns, so a CRLF checkout gives the same id), first 16 hex digits. RynthNav.TownNet
    computes it the same way. Equal ids mean equal data."""
    kept = [l.rstrip('\r') for l in text.split('\n') if not l.startswith('  "setId":') and not l.startswith('  "generated":')]
    return hashlib.sha256('\n'.join(kept).encode('utf-8')).hexdigest()[:16]


def serialize(doc):
    """One item per line (small diffs when the world changes); RynthNav.TownNet writes the same layout."""
    keys = ['version', 'setId', 'generated', 'source', 'landblocks', 'counts', 'notes', 'walkInfo']
    lists = ['arrivals', 'entries', 'exits', 'npcs', 'walks']
    items = [k for k in keys if k in doc] + [k for k in lists if k in doc] + \
        [k for k in doc if k not in keys and k not in lists]
    out = ['{\n']
    for i, k in enumerate(items):
        last = i == len(items) - 1
        v = doc[k]
        if k in lists:
            out.append('  %s: [\n' % json.dumps(k))
            for j, item in enumerate(v):
                out.append('    ' + json.dumps(item, ensure_ascii=False, separators=(',', ':')) +
                           (',' if j < len(v) - 1 else '') + '\n')
            out.append('  ]%s\n' % ('' if last else ','))
        else:
            out.append('  %s: %s%s\n' % (json.dumps(k), json.dumps(v, ensure_ascii=False), '' if last else ','))
    out.append('}\n')
    return ''.join(out)


def write(path, doc):
    doc['setId'] = ''
    doc['setId'] = set_id(serialize(doc))
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(serialize(doc))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--out', required=True, help='output folder for townnet.json')
    ap.add_argument('--host', default='aelrynth-vps')
    ap.add_argument('--db', default='aeshnidae_world')
    ap.add_argument('--dump', help='also save the raw query results here')
    ap.add_argument('--from-dump', help='read the raw query results from here instead of the server')
    ap.add_argument('--lb', help='the network landblocks, hex, comma separated (default: found from the portals)')
    args = ap.parse_args(argv)

    rows = fetch(args)
    lbs = [int(s, 16) for s in args.lb.split(',')] if args.lb else None
    net = build(rows, lbs)
    doc = {
        'version': FORMAT_VERSION,
        'setId': '',
        'generated': datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
        'source': 'world database',
        'landblocks': net['landblocks'],
        'counts': {'arrivals': len(net['arrivals']), 'entries': len(net['entries']), 'exits': len(net['exits']),
                   'npcs': len(net['npcs'])},
        'notes': net['notes'],
        'arrivals': net['arrivals'], 'entries': net['entries'], 'exits': net['exits'], 'npcs': net['npcs'],
    }
    os.makedirs(args.out, exist_ok=True)
    path = os.path.join(args.out, 'townnet.json')
    write(path, doc)
    print('Town Network landblocks: %s (portals named Town Network: weenies %s)'
          % (', '.join(net['landblocks']), ', '.join(map(str, net['townNetworkWeenies']))))
    print('arrivals %d, entry portals %d, exit portals %d, npcs %d'
          % (len(net['arrivals']), len(net['entries']), len(net['exits']), len(net['npcs'])))
    restricted = [e for e in net['entries'] + net['exits']
                  if any(k in e for k in ('minLevel', 'maxLevel', 'quest', 'closed', 'onlyOlthoi', 'noPk', 'noNpk'))]
    for e in restricted:
        print('  restricted: %s (%s)' % (e['name'], ', '.join('%s=%s' % (k, e[k]) for k in
              ('minLevel', 'maxLevel', 'quest', 'closed', 'onlyOlthoi', 'noPk', 'noNpk') if k in e)))
    for n in net['notes']:
        print('  note: ' + n)
    print('wrote %s (set %s); the walks inside come from RynthNav.TownNet build' % (path, doc['setId']))
    return 0


if __name__ == '__main__':
    sys.exit(main())
