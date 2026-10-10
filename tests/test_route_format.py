"""Portable route conversion and curated bundle invariants (standard library only)."""
from __future__ import annotations

import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("convert_routes", ROOT / "scripts/convert_routes.py")
assert SPEC is not None and SPEC.loader is not None
converter = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(converter)


def saved_route():
    return {
        "id": "stable-route-id", "name": "Asmodian · Episode 2 · Level 10–16",
        "repeat": False, "category": "Leveling Routes", "color": "#a78bfa",
        "stops": [
            {"map": "altgard", "x": 3395.644, "y": 2707.848, "name": "Finding Nemon",
             "kind": "NPCs · Hero Quest", "icon": "icons/quest.png", "petId": None, "color": "#facc15"},
            {"map": "altgard", "x": 3319.52, "y": 2749.1, "name": "",
             "kind": "Waypoint", "icon": None, "petId": None, "color": "#ffffff"},
            {"map": "altgard", "x": 3395.644, "y": 2707.848, "name": "Finding Nemon",
             "kind": "NPCs · Hero Quest", "icon": "icons/quest.png", "petId": None, "color": "#facc15"},
        ],
    }


class RouteConversionTests(unittest.TestCase):
    def test_identity_coordinates_repeated_visits_and_blank_titles_are_preserved(self):
        original = saved_route()
        before = copy.deepcopy(original)
        result = converter.convert([original])
        route = result["routes"][0]
        self.assertEqual(original, before)
        self.assertEqual(route["id"], original["id"])
        self.assertEqual(route["episode"], {"faction": "asmodian", "number": 2, "minLevel": 10, "maxLevel": 16})
        self.assertEqual([s["id"] for s in route["stops"]], [f"stable-route-id:{i}" for i in range(3)])
        self.assertEqual(route["stops"][1]["title"], "")
        for old, now in zip(original["stops"], route["stops"]):
            self.assertEqual((now["map"], now["x"], now["y"], now["title"], now["color"]),
                             (old["map"], old["x"], old["y"], old["name"], old["color"]))

    def test_verified_colour_takes_precedence_over_shared_npc_kind(self):
        for colour, expected in (("#facc15", "main-quest"), ("#4ade80", "regional-quest"),
                                 ("#a78bfa", "teleport"), ("#ffffff", "exploration")):
            with self.subTest(colour=colour):
                stop = saved_route()["stops"][0]
                stop["color"] = colour
                self.assertEqual(converter.objective(stop, True), expected)

    def test_nonleveling_routes_and_pet_markers_remain_custom(self):
        route = saved_route()
        route.update(name="Own route", category=None)
        route["stops"][0]["petId"] = "pet-123"
        converted = converter.convert([route])["routes"][0]
        self.assertNotIn("episode", converted)
        self.assertEqual(converted["stops"][0]["petId"], "pet-123")
        self.assertTrue(all(s["objective"] == "custom" for s in converted["stops"]))

    def test_leveling_only_filters_without_reordering(self):
        route = saved_route()
        own = copy.deepcopy(route)
        own.update(id="own", category=None)
        self.assertEqual(converter.convert([own, route], leveling_only=True), converter.convert([route]))
        document = converter.convert([own, route])
        self.assertEqual(converter.convert(document, leveling_only=True), converter.convert([route]))

    def test_envelope_roundtrip_is_lossless(self):
        document = converter.convert([saved_route()])
        self.assertEqual(converter.convert(json.loads(json.dumps(document))), document)

    def test_other_formats_are_rejected(self):
        with self.assertRaises(ValueError):
            converter.convert({"format": "other-format", "version": 1, "markers": []})

    def test_future_versions_and_other_coordinate_conventions_are_rejected(self):
        for field, value in (("version", 2), ("version", True), ("coordinates", "world")):
            with self.subTest(field=field, value=value):
                document = converter.convert([saved_route()])
                document[field] = value
                with self.assertRaises(ValueError):
                    converter.convert(document)

    def test_invalid_coordinate_duplicate_id_and_wrong_station_id_are_rejected(self):
        route = saved_route()
        for value in (float("nan"), float("inf"), -1, True, "123"):
            with self.subTest(coordinate=repr(value)):
                wrong = copy.deepcopy(route)
                wrong["stops"][0]["x"] = value
                with self.assertRaises(ValueError):
                    converter.convert([wrong])
        with self.assertRaises(ValueError):
            converter.convert([route, route])
        document = converter.convert([route])
        document["routes"][0]["stops"][0]["id"] = "other:0"
        with self.assertRaises(ValueError):
            converter.convert(document)

    def test_contradictory_episode_and_objective_metadata_are_rejected(self):
        for field, value in (("faction", "elyos"), ("number", 3), ("minLevel", 11), ("maxLevel", None)):
            with self.subTest(field=field, value=value):
                document = converter.convert([saved_route()])
                document["routes"][0]["episode"][field] = value
                with self.assertRaises(ValueError):
                    converter.convert(document)
        document = converter.convert([saved_route()])
        document["routes"][0]["stops"][0]["objective"] = "regional-quest"
        with self.assertRaises(ValueError):
            converter.convert(document)

    def test_cli_writes_separately_and_never_reads_character_state(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            source, destination = directory / "routes.json", directory / "routes.soulroute"
            source.write_text(json.dumps([saved_route()]), encoding="utf-8")
            unrelated = directory / "leveling-progress.json"
            unrelated.write_bytes(b"private character progress")
            before = source.read_bytes()
            result = subprocess.run([sys.executable, str(ROOT / "scripts/convert_routes.py"), str(source), str(destination)],
                                    capture_output=True, text=True, check=False)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(source.read_bytes(), before)
            self.assertEqual(unrelated.read_bytes(), b"private character progress")
            self.assertEqual(json.loads(destination.read_text(encoding="utf-8")), converter.convert([saved_route()]))
            result = subprocess.run([sys.executable, str(ROOT / "scripts/convert_routes.py"), str(destination), str(destination)],
                                    capture_output=True, text=True, check=False)
            self.assertEqual(result.returncode, 2)


class CuratedLevelingBundleTests(unittest.TestCase):
    def test_complete_bundle_keeps_the_reviewed_episode_ids_order_and_station_counts(self):
        path = ROOT / "data/routes/leveling.soulroute"
        document = converter.validate(json.loads(path.read_text(encoding="utf-8")))
        routes = document["routes"]
        expected = [
            ("373a3fb90c9596817acb7e16b5d2d581", "asmodian", 2, 82),
            ("f557635737106fd62e9248f5253ae3bf", "asmodian", 3, 81),
            ("4b3ff034339bc44060c7e9f0eeeaf6e2", "asmodian", 4, 106),
            ("2280d7af7d6157b9921eeb9aac6b6038", "asmodian", 5, 141),
            ("3d09b1604897bc16957a6152c9a669d6", "asmodian", 6, 68),
            ("68c7d5cfaacecd4de774d797c5e62511", "elyos", 2, 133),
            ("dc087e32bbb2122727951f9e07383eda", "elyos", 3, 40),
            ("b07d4242a350b645daa3384990204aa4", "elyos", 4, 69),
            ("908ed7d4e65f340024bb7748e0ba5c8b", "elyos", 5, 85),
            ("99b8ad15b1d27d5eb6eaf961df4e4df8", "elyos", 6, 62),
        ]
        self.assertEqual([(r["id"], r["episode"]["faction"], r["episode"]["number"], len(r["stops"])) for r in routes], expected)
        stops = [s for r in routes for s in r["stops"]]
        self.assertEqual(len(stops), 867)
        self.assertEqual(sum(not s["title"] for s in stops), 409)
        self.assertEqual(sum(s["title"] == "Empyrean Monolith" for s in stops), 6)
        self.assertFalse(any("empyrean trace" in (s["title"] + " " + s["kind"]).casefold() for s in stops))
        self.assertTrue(all(r["category"] == "Leveling Routes" for r in routes))
        self.assertTrue(all(set(s).issubset({"id", "map", "x", "y", "title", "objective", "kind", "icon", "petId", "color"}) for s in stops))
        self.assertTrue(all(set(r).issubset({"id", "name", "category", "repeat", "color", "episode", "stops"}) for r in routes))


if __name__ == "__main__":
    unittest.main()
