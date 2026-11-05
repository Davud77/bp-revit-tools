import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'exporter'))
from ac2r.export import story_structure  # noqa: E402
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from export_room_separators import same_source  # noqa: E402
from revit_session import choose_translator  # noqa: E402


class StoryStabilityTests(unittest.TestCase):
    """1.6.5 minvody export stopped because only the window's current story changed (actStory 0 -> 2)."""

    base = {'firstStory': -1, 'lastStory': 3, 'actStory': 0, 'skipNullFloor': True,
            'stories': [{'index': 0, 'floorId': 1, 'level': 0, 'height': 4.1, 'name': '1-й этаж'}]}

    def test_current_story_is_not_structure(self):
        self.assertEqual(story_structure(self.base), story_structure(dict(self.base, actStory=2)))

    def test_real_story_change_is_detected(self):
        changed = dict(self.base, stories=[dict(self.base['stories'][0], level=0.3)])
        self.assertNotEqual(story_structure(self.base), story_structure(changed))
        self.assertNotEqual(story_structure(self.base), story_structure(dict(self.base, lastStory=4)))


class SourceComparisonTests(unittest.TestCase):
    """Minvody: wall top link homeStoryDifference 0 -> -2 after the active story changed."""

    wall = {'begCoordinate': {'x': 1.0, 'y': 2.0}, 'height': 3.0, 'linkToSettings': {'homeStoryDifference': 0, 'topOffset': 0.2}}

    def test_view_relative_story_link_ignored(self):
        actual = {'begCoordinate': {'x': 1.0, 'y': 2.0}, 'height': 3.0, 'linkToSettings': {'homeStoryDifference': -2, 'topOffset': 0.2}}
        self.assertTrue(same_source(self.wall, actual))

    def test_real_change_still_detected(self):
        actual = {'begCoordinate': {'x': 1.0, 'y': 2.0}, 'height': 3.0, 'linkToSettings': {'homeStoryDifference': 0, 'topOffset': 0.3}}
        self.assertFalse(same_source(self.wall, actual))


class TranslatorChoiceTests(unittest.TestCase):
    """АР ветхое has no «Точный Экспорт Геометрии»; the export must still pick a translator and say which."""

    def test_requested_kept(self):
        self.assertEqual(choose_translator(['A', 'Точный Экспорт Геометрии'], 'Точный Экспорт Геометрии'), ('Точный Экспорт Геометрии', ''))

    def test_fallback_is_stated(self):
        name, note = choose_translator(['Экспорт в Revit MEP', 'Транслятор для Общего Экспорта'], 'Точный Экспорт Геометрии')
        self.assertEqual(name, 'Транслятор для Общего Экспорта')
        self.assertIn('отсутствует', note)
        self.assertEqual(choose_translator(['Мой точный IFC'], 'Точный Экспорт Геометрии')[0], 'Мой точный IFC')
        self.assertEqual(choose_translator(['Только этот'], 'X')[0], 'Только этот')
        with self.assertRaises(ValueError):
            choose_translator([], 'X')


if __name__ == '__main__':
    unittest.main()
