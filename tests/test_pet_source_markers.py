import copy
import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from build_tester_data import add_pet_source_markers

class PetSourcesTests(unittest.TestCase):
    def test_only_known_sources_and_no_duplicate_or_replacement(self):
        pets = {"slime": {"id": "slime", "en": "Slime", "de": None,
                          "genus": "natura", "icon": "icons/slime.png"}}
        data = {"categories": [{"group": "Monsters"}], "groups": [], "icons": [],
                "markers": [[0, 12, 34, "Abyss Slime", None, -1, "slime"],
                            [0, 12, 34, "Abyss Slime", None, -1, "slime"],
                            [0, 50, 60, "Unknown", None, -1, None]]}
        original = copy.deepcopy(data["markers"])
        add_pet_source_markers(data, pets)
        self.assertEqual(data["markers"][:3], original)
        self.assertEqual(len(data["markers"]), 4)
        self.assertEqual(data["markers"][-1][1:3], [12, 34])
        self.assertEqual(data["markers"][-1][7]["en"], "Abyss Slime")
        self.assertEqual(data["icons"][data["markers"][-1][5]], "icons/slime.png")
        self.assertFalse(data["categories"][-1]["hidden"])
        snapshot = copy.deepcopy(data)
        add_pet_source_markers(data, pets)
        self.assertEqual(data, snapshot)

    def test_missing_portrait_does_not_create_broken_icon(self):
        data = {"categories": [{"group": "Monsters"}], "groups": [], "icons": [],
                "markers": [[0, 1, 2, "Slime", None, -1, "slime"]]}
        add_pet_source_markers(data, {"slime": {"id": "slime", "icon": None}})
        self.assertEqual(len(data["markers"]), 1)

    def test_cluster_gives_one_symbol_per_spawn_group(self):
        pets = {"swarm": {"id": "swarm", "en": "Swarm", "genus": "varian", "icon": "icons/swarm.png"}}
        data = {"categories": [{"group": "Monsters"}], "groups": [], "icons": [],
                "markers": [[0, 0, 0, "Abyss Swarm", None, -1, "swarm"],
                            [0, 4, 0, "Abyss Swarm", None, -1, "swarm"],
                            [0, 8, 0, "Abyss Swarm", None, -1, "swarm"],
                            [0, 100, 0, "Abyss Swarm", None, -1, "swarm"]]}
        add_pet_source_markers(data, pets, cluster=0.05)  # radius 5 px over a 100 px area
        symbols = data["markers"][4:]
        self.assertEqual([m[1:3] for m in symbols], [[4, 0], [100, 0]])
        self.assertEqual(symbols[0][7]["en"], "Abyss Swarm (3×)")
        self.assertEqual(symbols[1][7]["en"], "Abyss Swarm")

if __name__ == "__main__":
    unittest.main()
