import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'exporter'))
from ac2r.jsonio import write_json, long_path  # noqa: E402


class LongPathTests(unittest.TestCase):
    """1.6.3 failed on a long project name: the temporary JSON crossed MAX_PATH."""

    @unittest.skipUnless(os.name == 'nt', 'MAX_PATH applies to Windows')
    def test_write_json_beyond_max_path(self):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, '\\\\?\\' + os.path.abspath(root), True)
        if True:
            deep = Path(root)
            while len(str(deep)) < 250:
                deep = deep / 'МКД_8 эт_Кизляр_Общая модель_'
            target = deep / 'missing-wall-ifc' / 'wall-bodies.json'
            self.assertGreater(len(str(target)), 260)
            write_json(target, {'bodies': [1, 2, 3]}, compact=True)
            with open(long_path(target), encoding='utf-8') as stream:
                self.assertEqual(json.load(stream), {'bodies': [1, 2, 3]})
            leftovers = [p for p in os.listdir(long_path(target.parent)) if p.endswith('.tmp')]
            self.assertEqual(leftovers, [])

    def test_short_paths_unchanged(self):
        self.assertEqual(long_path('a/b.json'), Path('a/b.json'))


if __name__ == '__main__':
    unittest.main()
