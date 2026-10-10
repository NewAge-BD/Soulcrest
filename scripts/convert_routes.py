"""Convert Soulcrest's saved routes to the versioned, portable .soulroute format.

Only SavedRoute arrays and soulcrest.routes documents are accepted; any other format is rejected.
Character progress and running markers are deliberately never read or written.
"""
from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import re
import tempfile
from typing import Any


FORMAT = "soulcrest.routes"
VERSION = 1
COORDINATES = "map-pixels"
CATEGORY = "Leveling Routes"
OBJECTIVES = {"main-quest", "regional-quest", "teleport", "exploration", "waypoint", "custom"}
EPISODE_NAME = re.compile(
    r"^(Asmodian|Elyos) · Episode ([1-9][0-9]*)(?: · Level ([1-9][0-9]*)–([1-9][0-9]*))?$"
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def required_text(value: Any, field: str) -> str:
    require(isinstance(value, str) and bool(value.strip()), f"{field} must be a nonempty string.")
    return value


def coordinate(value: Any, field: str) -> int | float:
    require(isinstance(value, (int, float)) and not isinstance(value, bool)
            and math.isfinite(value) and value >= 0, f"{field} must be a finite, nonnegative number.")
    return value


def objective(stop: dict[str, Any], leveling: bool) -> str:
    if not leveling:
        return "custom"
    # The reviewed colour takes precedence: one NPC may offer different quests.
    colour = stop.get("color")
    if colour == "#facc15":
        return "main-quest"
    if colour == "#4ade80":
        return "regional-quest"
    if colour == "#a78bfa":
        return "teleport"
    kind = stop.get("kind", "")
    if colour == "#ffffff":
        return "waypoint" if kind == "Waypoint" else "exploration"
    text = (kind + " " + stop.get("name", "")).casefold()
    if "kibelisk" in text or "telepor" in text:
        return "teleport"
    if "sealed dungeon" in text or "stronghold" in text:
        return "exploration"
    if stop.get("name", "").casefold().startswith("main quest"):
        return "main-quest"
    if stop.get("name", "").casefold().startswith("side quest") or "regional quest" in text:
        return "regional-quest"
    if "hero quest" in text:
        return "main-quest"
    return "waypoint" if kind == "Waypoint" else "exploration"


def episode(route: dict[str, Any]) -> dict[str, Any] | None:
    if route.get("category") != CATEGORY:
        return None
    match = EPISODE_NAME.fullmatch(route.get("name", ""))
    if not match:
        return None
    result: dict[str, Any] = {
        "faction": "asmodian" if match[1] == "Asmodian" else "elyos",
        "number": int(match[2]),
    }
    if match[3] is not None:
        result.update(minLevel=int(match[3]), maxLevel=int(match[4]))
    return result


def validate(document: Any) -> dict[str, Any]:
    require(isinstance(document, dict), "Expected a Soulcrest routes document.")
    require(document.get("format") == FORMAT, "Unsupported route format; raw marker imports are not accepted.")
    require(type(document.get("version")) is int and document["version"] == VERSION, "Unsupported route format version.")
    require(document.get("coordinates") == COORDINATES, "Unsupported route coordinate convention.")
    routes = document.get("routes")
    require(isinstance(routes, list), "routes must be an array.")
    route_ids: set[str] = set()
    for route in routes:
        require(isinstance(route, dict), "Every route must be an object.")
        rid = required_text(route.get("id"), "route.id")
        require(rid not in route_ids, f"Duplicate route ID: {rid}.")
        route_ids.add(rid)
        required_text(route.get("name"), "route.name")
        require(type(route.get("repeat")) is bool, "route.repeat must be a boolean.")
        for key in ("category", "color"):
            require(route.get(key) is None or isinstance(route[key], str), f"route.{key} must be a string or null.")
        metadata = route.get("episode")
        if metadata is not None:
            require(isinstance(metadata, dict), "route.episode must be an object.")
            require(metadata.get("faction") in ("asmodian", "elyos"), "Unknown episode faction.")
            require(type(metadata.get("number")) is int and metadata["number"] > 0, "Episode number must be positive.")
            for key in ("minLevel", "maxLevel"):
                require(metadata.get(key) is None or type(metadata[key]) is int and metadata[key] > 0,
                        f"episode.{key} must be positive or null.")
            require(metadata.get("minLevel") is None or metadata.get("maxLevel") is None
                    or metadata["minLevel"] <= metadata["maxLevel"], "Episode level range is reversed.")
            expected_episode = episode(route)
            require(expected_episode is not None
                    and all(metadata.get(key) == expected_episode.get(key)
                            for key in ("faction", "number", "minLevel", "maxLevel")),
                    "Episode metadata disagrees with the reviewed route name and category.")
        stops = route.get("stops")
        require(isinstance(stops, list), "route.stops must be an array.")
        for index, stop in enumerate(stops):
            require(isinstance(stop, dict), "Every stop must be an object.")
            require(stop.get("id") == f"{rid}:{index}", "Stop IDs must contain the route ID and zero-based index.")
            required_text(stop.get("map"), "stop.map")
            coordinate(stop.get("x"), "stop.x")
            coordinate(stop.get("y"), "stop.y")
            require(isinstance(stop.get("title"), str), "stop.title must be a string; an empty title is valid.")
            required_text(stop.get("kind"), "stop.kind")
            require(stop.get("objective") in OBJECTIVES, "Unknown stop objective.")
            for key in ("icon", "petId", "color"):
                require(stop.get(key) is None or isinstance(stop[key], str), f"stop.{key} must be a string or null.")
            expected_objective = objective({**stop, "name": stop["title"]}, route.get("category") == CATEGORY)
            require(stop["objective"] == expected_objective,
                    "Stop objective disagrees with the reviewed colour, kind and route category.")
    return document


def convert(value: Any, *, leveling_only: bool = False) -> dict[str, Any]:
    if isinstance(value, dict):
        document = validate(value)
        if leveling_only:
            document = {**document, "routes": [r for r in document["routes"] if r.get("category") == CATEGORY]}
        return document
    require(isinstance(value, list), "Expected a SavedRoute array or a Soulcrest routes document.")
    routes = []
    for original in value:
        require(isinstance(original, dict), "Every saved route must be an object.")
        if leveling_only and original.get("category") != CATEGORY:
            continue
        rid = required_text(original.get("id"), "route.id")
        route = {"id": rid, "name": original.get("name"), "repeat": original.get("repeat", False)}
        for key in ("category", "color"):
            if original.get(key) is not None:
                route[key] = original[key]
        metadata = episode(original)
        if metadata is not None:
            route["episode"] = metadata
        require(isinstance(original.get("stops"), list), "Saved route.stops must be an array.")
        stops = []
        for index, original_stop in enumerate(original["stops"]):
            require(isinstance(original_stop, dict), "Every saved stop must be an object.")
            stop = {
                "id": f"{rid}:{index}",
                "map": original_stop.get("map"),
                "x": original_stop.get("x"),
                "y": original_stop.get("y"),
                "title": original_stop.get("name"),
                "objective": objective(original_stop, original.get("category") == CATEGORY),
                "kind": original_stop.get("kind"),
            }
            for key in ("icon", "petId", "color"):
                if original_stop.get(key) is not None:
                    stop[key] = original_stop[key]
            stops.append(stop)
        route["stops"] = stops
        routes.append(route)
    return validate({"format": FORMAT, "version": VERSION, "coordinates": COORDINATES, "routes": routes})


def write_atomic(path: Path, document: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="\n", dir=path.parent,
                                         prefix=path.name + ".", suffix=".tmp", delete=False) as handle:
            temporary = Path(handle.name)
            json.dump(document, handle, ensure_ascii=False, indent=2, allow_nan=False)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Curated SavedRoute JSON array or existing .soulroute file")
    parser.add_argument("destination", type=Path, help="Destination .soulroute file")
    parser.add_argument("--leveling-only", action="store_true", help="Export only Leveling Routes")
    args = parser.parse_args()
    try:
        require(args.source.resolve() != args.destination.resolve(), "Use a separate destination; source data stays untouched.")
        require(args.destination.suffix == ".soulroute", "Destination must have the .soulroute extension.")
        value = json.loads(args.source.read_text(encoding="utf-8-sig"))
        result = convert(value, leveling_only=args.leveling_only)
        write_atomic(args.destination, result)
    except (OSError, ValueError, TypeError) as error:
        parser.exit(2, f"Conversion failed: {error}\n")
    print(f"Converted {len(result['routes'])} routes / {sum(len(r['stops']) for r in result['routes'])} stops.")


if __name__ == "__main__":
    main()
