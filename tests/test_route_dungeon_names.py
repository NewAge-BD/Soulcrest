"""Exact offline dungeon-name joins and the reviewed default-title baseline."""
from __future__ import annotations

import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("name_route_dungeons", ROOT / "scripts/name_route_dungeons.py")
assert SPEC is not None and SPEC.loader is not None
namer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(namer)


def fixture():
    point = {"id": "object-point-1", "types": ["dungeon"], "name": "Unknown Portal",
             "target": {"mainCategoryId": "maps", "id": "840017", "name": "Ancient Ruins"}}
    stop = {"id": "route-1:0", "map": "altgard", "x": 100.125, "y": 200.875,
            "title": "Original quest", "kind": "Locations · Dungeon", "color": "#ffffff",
            "objective": "exploration"}
    document = {"routes": [{"id": "route-1", "category": "Leveling Routes", "stops": [stop]}]}
    key = ("Dungeon", stop["x"], stop["y"])
    markers = {key: [[1, stop["x"], stop["y"], point["name"]]]}
    return document, {"altgard": ({key: [point]}, markers, {}, "1110")}, point, key


class RouteDungeonNameTests(unittest.TestCase):
    def test_target_name_is_used_and_only_title_changes(self):
        document, catalogs, _, _ = fixture()
        before = copy.deepcopy(document)
        result, manifest = namer.name_stops(document, catalogs)
        self.assertEqual(document, before)
        expected = copy.deepcopy(document)
        expected["routes"][0]["stops"][0]["title"] = "Ancient Ruins"
        self.assertEqual(result, expected)
        self.assertEqual(manifest["stops"][0]["previousTitle"], "Original quest")
        self.assertEqual(manifest["stops"][0]["targetId"], "840017")
        self.assertFalse(manifest["unresolved"])

    def test_close_coordinates_and_wrong_categories_never_match(self):
        for key in (("Dungeon", 100.126, 200.875), ("Sealed Dungeon", 100.125, 200.875)):
            document, catalogs, point, _ = fixture()
            old = catalogs["altgard"]
            catalogs["altgard"] = {key: [point]}, old[1], old[2], old[3]
            result, manifest = namer.name_stops(document, catalogs)
            self.assertEqual(result, document)
            self.assertFalse(manifest["stops"])
            self.assertEqual(manifest["unresolved"][0]["reason"], "no-unique-exact-marker")

    def test_ambiguous_source_or_generated_marker_is_left_unchanged(self):
        for duplicate in (0, 1):
            document, catalogs, _, key = fixture()
            catalogs["altgard"][duplicate][key] *= 2
            result, manifest = namer.name_stops(document, catalogs)
            self.assertEqual(result, document)
            self.assertFalse(manifest["stops"])

    def test_npc_in_dungeon_category_does_not_become_a_dungeon_title(self):
        document, catalogs, point, key = fixture()
        point.pop("target")
        point.update(id="npc-point-1", name="Matir", entity={"mainCategoryId": "npcs", "id": "2950033"})
        catalogs["altgard"][1][key][0][3] = "Matir"
        result, manifest = namer.name_stops(document, catalogs)
        self.assertEqual(result, document)
        self.assertEqual(manifest["unresolved"][0]["reason"], "npc-without-dungeon-target")

    def test_instance_layer_door_uses_its_exact_source_name(self):
        document, catalogs, point, key = fixture()
        point.pop("target")
        point["name"] = "Caelid's Office Door"
        catalogs["altgard"][1][key][0][3] = point["name"]
        catalogs["altgard"][2]["point-1"] = {"kind": "enterinstancelayer", "name": point["name"], "objectId": "1200002"}
        result, manifest = namer.name_stops(document, catalogs)
        self.assertEqual(result["routes"][0]["stops"][0]["title"], "Caelid's Office Door")
        self.assertEqual(manifest["stops"][0]["targetCategory"], "objects")

    def test_unconfirmed_door_or_disagreeing_generated_name_is_rejected(self):
        document, catalogs, point, key = fixture()
        point.pop("target")
        result, manifest = namer.name_stops(document, catalogs)
        self.assertEqual(result, document)
        self.assertEqual(manifest["unresolved"][0]["reason"], "no-dungeon-target")
        catalogs["altgard"][1][key][0][3] = "Different cached source"
        with self.assertRaises(ValueError):
            namer.name_stops(document, catalogs)

    def test_regeneration_retains_original_title_and_requires_review_for_new_names(self):
        document, catalogs, point, _ = fixture()
        result, manifest = namer.name_stops(document, catalogs)
        again, repeated = namer.name_stops(result, catalogs, manifest)
        self.assertEqual(result, again)
        self.assertEqual(manifest, repeated)
        point["target"]["name"] = "Changed source target"
        with self.assertRaises(ValueError):
            namer.name_stops(result, catalogs, manifest)

    def test_nonleveling_routes_and_individual_edits_are_protected(self):
        document, catalogs, _, _ = fixture()
        document["routes"][0]["category"] = None
        result, manifest = namer.name_stops(document, catalogs)
        self.assertEqual(result, document)
        self.assertFalse(manifest["stops"])
        document["routes"][0]["category"] = "Leveling Routes"
        _, manifest = namer.name_stops(document, catalogs)
        document["routes"][0]["stops"][0]["title"] = "My edited title"
        with self.assertRaises(ValueError):
            namer.name_stops(document, catalogs, manifest)

    def test_coloured_quest_stations_keep_both_existing_and_blank_titles(self):
        for colour, objective in (("#facc15", "main-quest"), ("#4ade80", "regional-quest")):
            for title in ("Original quest", ""):
                document, catalogs, _, _ = fixture()
                stop = document["routes"][0]["stops"][0]
                stop.update(color=colour, objective=objective, title=title)
                result, manifest = namer.name_stops(document, catalogs)
                self.assertEqual(result, document)
                self.assertFalse(manifest["stops"])
                self.assertFalse(manifest["unresolved"])

    def test_json_writer_preserves_existing_bom_and_line_endings(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "routes.soulroute"
            for encoding, ending in (("utf-8-sig", "\r\n"), ("utf-8", "\n")):
                path.write_bytes(("{\n}\n".replace("\n", ending)).encode(encoding))
                namer.write_preserving(path, {"name": "Versteck"})
                raw = path.read_bytes()
                self.assertEqual(raw.startswith(b"\xef\xbb\xbf"), encoding == "utf-8-sig")
                self.assertEqual(b"\r\n" in raw, ending == "\r\n")
                self.assertEqual(json.loads(raw), {"name": "Versteck"})

    def test_reviewed_manifest_matches_bundle_stop_ids_and_names(self):
        document = json.loads((ROOT / "data/routes/leveling.soulroute").read_text(encoding="utf-8-sig"))
        manifest = json.loads((ROOT / "data/routes/dungeon-title-upgrades.json").read_text(encoding="utf-8-sig"))
        stops = {stop["id"]: stop for route in document["routes"] for stop in route["stops"]}
        self.assertEqual(len(stops), 867)
        self.assertEqual(len(manifest["stops"]), 55)
        self.assertEqual(len(manifest["unresolved"]), 0)
        self.assertEqual(sum(row["previousTitle"] != row["title"] for row in manifest["stops"]), 55)
        for row in manifest["stops"]:
            stop = stops[row["id"]]
            for key in ("title", "map", "x", "y", "kind"):
                self.assertEqual(row[key], stop[key])
            self.assertTrue(row["pointId"].startswith("object-"))
            self.assertTrue(all(ref.startswith(namer.CDN) for ref in row["sourceRefs"]))
        for row in manifest["unresolved"]:
            self.assertNotIn(row["id"], {entry["id"] for entry in manifest["stops"]})


if __name__ == "__main__":
    unittest.main()
