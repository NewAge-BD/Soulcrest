"""Name bundled dungeon stops through exact, offline gaming.tools point/target joins.

No nearest-position matching, network access or user library access. The accompanying upgrade
manifest retains the reviewed original titles so runtime updates can protect user edits.
"""
from __future__ import annotations

import argparse
from collections import defaultdict
import copy
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
CDN = "https://cdn-hosted.gaming.tools/aion2/data/en/"


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def pixel(config, size, x, y):
    """Same map boundary conversion as build_tester_data.to_pixel, rounded at the import edge."""
    if config.get("transformType", "original") != "original":
        raise ValueError("Unsupported coordinate transform")
    bounds = config["bounds"]
    width, height = config["imageWidth"], config["imageHeight"]
    dx, dy = bounds["maxX"] - bounds["minX"], bounds["maxY"] - bounds["minY"]
    if min(dx, dy) <= 0:
        raise ValueError("Invalid map bounds")
    scale = min(width / dx, height / dy)
    logical = size / (512 * 2 ** config["maximumZoom"])
    return (round(((width - dx * scale) / 2 + (x - bounds["minX"]) * scale) * logical, 3),
            round(((height - dy * scale) / 2 + (y - bounds["minY"]) * scale) * logical, 3))


def read_map(path: Path, map_id: str):
    text = path.read_text(encoding="utf-8-sig")
    prefix = f'window.SoulcrestMaps["{map_id}"] = '
    lines = text.splitlines()
    assignments = [line[len(prefix):] for line in lines if line.startswith(prefix)]
    if len(assignments) != 1:
        raise ValueError("Unexpected generated map assignment")
    return json.loads(assignments[0].removesuffix(";"))


def name_stops(document, catalogs, previous=None):
    """Catalogs contain preprojected source points and generated markers, keyed by map ID."""
    result = copy.deepcopy(document)
    old_entries = {row["id"]: row for row in (previous or {}).get("stops", [])}
    updates, unresolved = [], []
    for route in result["routes"]:
        if route.get("category") != "Leveling Routes":
            continue
        for stop in route["stops"]:
            kind = stop["kind"].split(" · ")[-1]
            # User clarification 2026-10-12: white exploration stops, not coloured quest stations.
            if (kind not in ("Dungeon", "Sealed Dungeon") or stop.get("color") != "#ffffff"
                    or stop.get("objective") != "exploration"):
                continue
            points, markers, objects, source_id = catalogs[stop["map"]]
            key = (kind, stop["x"], stop["y"])
            matches, generated = points.get(key, []), markers.get(key, [])
            common = {"id": stop["id"], "map": stop["map"], "x": stop["x"], "y": stop["y"],
                      "kind": stop["kind"]}
            if len(matches) != 1 or len(generated) != 1:
                unresolved.append({**common, "reason": "no-unique-exact-marker"})
                continue
            point = matches[0]
            marker_name = point.get("name") or point.get("entity", {}).get("name")
            if marker_name != generated[0][3]:
                raise ValueError("Generated marker and cached point names disagree")
            target = point.get("target", {})
            obj = objects.get(point["id"].removeprefix("object-"), {})
            title = target.get("name")
            category, target_id = target.get("mainCategoryId"), target.get("id")
            if point.get("entity", {}).get("mainCategoryId") == "npcs":
                unresolved.append({**common, "pointId": point["id"], "reason": "npc-without-dungeon-target",
                                   "entityId": point["entity"]["id"]})
                continue
            if not (point["id"].startswith("object-") and category in ("activities", "maps")
                    and target_id and title):
                # An instance-layer doorway has an explicit entrance name but no target map.
                # Retain that source text verbatim; never remove "Door" to invent a map title.
                if obj.get("kind") == "enterinstancelayer" and obj.get("name") == point.get("name"):
                    title, category, target_id = point["name"], "objects", obj["objectId"]
                else:
                    unresolved.append({**common, "pointId": point["id"], "reason": "no-dungeon-target"})
                    continue
            prior = old_entries.get(stop["id"])
            if prior is not None and (prior["title"] != title or stop["title"] not in (prior["previousTitle"], title)):
                raise ValueError("Reviewed title baseline changed; inspect the source update explicitly")
            original_title = prior["previousTitle"] if prior else stop["title"]
            updates.append({**common, "previousTitle": original_title, "title": title,
                            "pointId": point["id"], "targetCategory": category, "targetId": target_id,
                            "sourceRefs": [CDN + f"interactive-map/{source_id}.d.json",
                                           CDN + f"maps/{source_id}.d.json"]})
            stop["title"] = title
    manifest = {"format": "soulcrest.dungeon-title-upgrades", "version": 1,
                "stops": updates, "unresolved": unresolved}
    return result, manifest


def load_catalogs(gamingtools: Path, mapdata: Path):
    data = gamingtools / "cdn/data/en"
    configs = {m["id"]: m["map"] for m in read_json(data / "interactive-map.json")["maps"]}
    catalogs = {}
    for definition in read_json(mapdata / "manifest.json")["maps"]:
        map_id, source_id = definition["id"], str(definition["sourceMapId"])
        generated = read_map(mapdata / map_id / "data.js", map_id)
        points, markers = defaultdict(list), defaultdict(list)
        source = read_json(data / f"interactive-map/{source_id}.json")
        for point in source["points"]:
            kinds = point["types"]
            kind = kinds[0] if kinds else ""
            if kind not in ("dungeon", "sealed-dungeon"):
                continue
            x, y = pixel(configs[source_id], generated["size"], point["x"], point["y"])
            points[("Sealed Dungeon" if kind == "sealed-dungeon" else "Dungeon", x, y)].append(point)
        for marker in generated["markers"]:
            kind = generated["categories"][marker[0]]["name"]
            if kind in ("Dungeon", "Sealed Dungeon"):
                markers[(kind, marker[1], marker[2])].append(marker)
        objects = {o["id"]: o for o in read_json(data / f"maps/{source_id}.json").get("objects", [])}
        catalogs[map_id] = points, markers, objects, source_id
    return catalogs


def write_preserving(path: Path, document):
    original = path.read_bytes() if path.exists() else b""
    ending = "\r\n" if b"\r\n" in original or not original else "\n"
    text = (json.dumps(document, ensure_ascii=False, indent=2, allow_nan=False) + "\n").replace("\n", ending)
    path.write_bytes((b"\xef\xbb\xbf" if original.startswith(b"\xef\xbb\xbf") else b"") + text.encode("utf-8"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gamingtools", type=Path, default=ROOT / "imports/gamingtools")
    parser.add_argument("--mapdata", type=Path, default=ROOT / "imports/generated/mapdata")
    parser.add_argument("--routes", type=Path, default=ROOT / "data/routes/leveling.soulroute")
    parser.add_argument("--upgrades", type=Path, default=ROOT / "data/routes/dungeon-title-upgrades.json")
    parser.add_argument("--write", action="store_true", help="Write the bundled titles and reviewed upgrade manifest")
    args = parser.parse_args()
    previous = read_json(args.upgrades) if args.upgrades.exists() else None
    result, manifest = name_stops(read_json(args.routes), load_catalogs(args.gamingtools, args.mapdata), previous)
    if args.write:
        write_preserving(args.routes, result)
        write_preserving(args.upgrades, manifest)
    elif result != read_json(args.routes) or previous != manifest:
        parser.exit(1, "Bundled dungeon names or upgrade manifest need an explicit source review.\n")
    print(f"Verified {len(manifest['stops'])} dungeon stops; {len(manifest['unresolved'])} left unchanged.")


if __name__ == "__main__":
    main()
