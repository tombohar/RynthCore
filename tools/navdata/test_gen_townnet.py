"""Offline tests for gen_townnet.py: a small made-up world through build(), the
restriction fields, and the file's set id. No server needed.

Run: python tools/navdata/test_gen_townnet.py
"""

import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gen_townnet as t  # noqa: E402

TN = 0x0007
HOLT = 0xA9B4


def portal(guid, wcid, cell, x, y, z, name, dcell, dx, dy, dz, minlvl=None, maxlvl=None, quest=None,
           bitmask='1', acct=None, advocate=None, setup='33554867', scale=None):
    """A row of the tn_portals query, as parse_rows returns it (strings, None for NULL)."""
    s = lambda v: None if v is None else str(v)  # noqa: E731
    return [str(guid), str(wcid), str(cell), s(x), s(y), s(z), '1', '0', '0', '0', name,
            str(dcell), s(dx), s(dy), s(dz), s(minlvl), s(maxlvl), quest, s(bitmask), s(acct), s(advocate), setup, s(scale)]


def world_rows():
    return {
        'tn_weenies': [['42852', 'Portal to Town Network', str((TN << 16) | 0x0145)],
                       ['30000', 'Some Other Portal', str((0x0123 << 16) | 0x0100)]],
        'tn_portals': [
            # Two entries outdoors in Holtburg landing at one arrival point, one placed twice.
            portal(0x7A9B4001, 42852, (HOLT << 16) | 0x0019, 84.0, 9.0, 94.2, 'Portal to Town Network',
                   (TN << 16) | 0x0145, 70.0, -80.0, 0.005, bitmask='129'),
            portal(0x7A9B4002, 42852, (HOLT << 16) | 0x0019, 84.0, 9.0, 94.2, 'Portal to Town Network',
                   (TN << 16) | 0x0145, 70.0, -80.0, 0.005, bitmask='129'),
            # A custom entry with a different name, landing elsewhere inside.
            portal(0x7A9B4003, 99001, (HOLT << 16) | 0x0020, 90.0, 20.0, 94.2, 'Aelrynth Gate',
                   (TN << 16) | 0x0143, 70.0, -60.0, 0.005),
            # Exits inside: an open one, a level-gated one, a quest one, a closed one.
            portal(0x70007028, 42820, (TN << 16) | 0x0124, 53.91, -39.95, -0.063, 'Portal to Holtburg',
                   (HOLT << 16) | 0x0019, 84.0, 7.1, 94.0),
            portal(0x70007084, 42839, (TN << 16) | 0x016B, 86.51, -170.05, -0.063, 'Portal to Eastwatch',
                   (0x49F0 << 16) | 0x0013, 70.0, 70.0, 170.0, minlvl=80),
            portal(0x70007090, 50001, (TN << 16) | 0x0171, 110.0, -53.37, -0.063, 'Secret Portal',
                   (HOLT << 16) | 0x0019, 84.0, 7.1, 94.0, quest='SecretFlag', acct=1),
            portal(0x70007091, 50002, (TN << 16) | 0x0172, 110.0, -60.0, -0.063, 'Shut Portal',
                   (HOLT << 16) | 0x0019, 84.0, 7.1, 94.0, bitmask='0'),
            # A portal elsewhere that has nothing to do with the network.
            portal(0x7A9B4009, 30001, (HOLT << 16) | 0x0021, 10.0, 10.0, 94.0, 'Portal to Somewhere',
                   (0x0123 << 16) | 0x0100, 1.0, 1.0, 0.0),
        ],
        'tn_npcs': [['1879076865', '42799', '12', str((TN << 16) | 0x0200), '10.0', '-20.0', '12.0', 'Barkeeper', '33554510'],
                    ['1879076866', '1', '10', str((HOLT << 16) | 0x0100), '1.0', '1.0', '1.0', 'Elsewhere', None]],
        'poi': [['Holtburg', '42820', str((HOLT << 16) | 0x0019), '84.0', '7.1', '94.0']],
        'surface': [[str(HOLT), '90.0', '100.0', '40']],
    }


class BuildTests(unittest.TestCase):
    def setUp(self):
        self.net = t.build(world_rows())

    def test_landblocks_from_portal_names(self):
        self.assertEqual(self.net['landblocks'], ['0007'])

    def test_arrivals(self):
        arr = self.net['arrivals']
        self.assertEqual([a['id'] for a in arr], ['A1', 'A2'])
        self.assertEqual(arr[0]['cell'], '00070143')       # sorted by cell
        self.assertEqual(arr[1]['entries'], 1)               # the doubled portal counts once

    def test_entries(self):
        en = self.net['entries']
        self.assertEqual(len(en), 2)
        holt = [e for e in en if e['name'] == 'Portal to Town Network'][0]
        self.assertEqual(holt['guids'], ['7A9B4001', '7A9B4002'])
        self.assertEqual(holt['arrival'], 'A2')
        self.assertEqual(holt['place'], 'outdoor')
        self.assertEqual(holt['town']['name'], 'Holtburg')
        self.assertTrue(holt['noOlthoi'])
        custom = [e for e in en if e['name'] == 'Aelrynth Gate'][0]
        self.assertEqual(custom['arrival'], 'A1')
        self.assertNotIn('guids', custom)

    def test_exits_and_restrictions(self):
        ex = {e['name']: e for e in self.net['exits']}
        self.assertEqual(len(ex), 4)
        self.assertEqual(ex['Portal to Holtburg']['label'], 'Holtburg')
        self.assertEqual(ex['Portal to Holtburg']['dest']['town']['name'], 'Holtburg')
        self.assertNotIn('minLevel', ex['Portal to Holtburg'])
        self.assertEqual(ex['Portal to Eastwatch']['minLevel'], 80)
        self.assertNotIn('town', ex['Portal to Eastwatch']['dest'])   # no town that close
        self.assertEqual(ex['Secret Portal']['quest'], 'SecretFlag')
        self.assertEqual(ex['Secret Portal']['accountRequirements'], 1)
        self.assertTrue(ex['Shut Portal']['closed'])
        self.assertEqual(ex['Portal to Holtburg']['setup'], '020001B3')

    def test_npcs_inside_only(self):
        self.assertEqual([n['name'] for n in self.net['npcs']], ['Barkeeper'])

    def test_lb_override_is_noted(self):
        net = t.build(world_rows(), [0x0007, 0x0008])
        self.assertEqual(net['landblocks'], ['0007', '0008'])
        self.assertTrue(any('--lb' in n for n in net['notes']))


def read(path):
    with open(path, encoding='utf-8') as f:
        return f.read()


class FileTests(unittest.TestCase):
    def test_set_id_ignores_timestamp(self):
        doc = {'version': 1, 'setId': '', 'generated': 'a', 'arrivals': [{'id': 'A1'}]}
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, 'townnet.json')
            t.write(p, dict(doc))
            first = json.loads(read(p))['setId']
            t.write(p, dict(doc, generated='b'))
            second = json.loads(read(p))['setId']
            t.write(p, dict(doc, arrivals=[{'id': 'A2'}]))
            third = json.loads(read(p))['setId']
            text = read(p)
        self.assertEqual(first, second)
        self.assertNotEqual(first, third)
        self.assertEqual(len(first), 16)
        self.assertIn('    {"id":"A2"}\n', text)
        self.assertEqual(t.set_id(text), json.loads(text)['setId'])
        self.assertEqual(t.set_id(text.replace('\n', '\r\n')), json.loads(text)['setId'])   # a CRLF checkout

    def test_heading(self):
        self.assertEqual(t.heading('1', '0'), 0.0)
        self.assertEqual(t.heading('0.70710678', '-0.70710678'), 90.0)   # yaw -90 = facing east


if __name__ == '__main__':
    unittest.main()
