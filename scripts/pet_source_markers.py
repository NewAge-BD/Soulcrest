import numpy as np

def add_pet_source_markers(payload, pets, cluster=None):
    """Show known soul sources where the import has no dedicated marker for that pet.

    Keep original monster markers and attach the source name as marker field 7.
    Never infer new pet associations or coordinates here. With cluster (fraction of the monster area),
    one symbol stands for a whole spawn group of that pet and names the number of monsters.
    """
    present = {m[6] for m in payload["markers"] if payload["categories"][m[0]]["group"] == "Pets"}
    categories = {}
    added = []
    seen = set()
    candidates = {}  # pet id -> monster markers in map order
    for marker in payload["markers"]:
        pet = pets.get(marker[6])
        if (payload["categories"][marker[0]]["group"] != "Monsters"
                or not pet or pet["id"] in present or not pet.get("icon")):
            continue
        key = (pet["id"], marker[1], marker[2])
        if key in seen:
            continue
        seen.add(key)
        candidates.setdefault(pet["id"], []).append(marker)
    radius = None
    if cluster and candidates:
        points = np.array([m[1:3] for m in payload["markers"] if payload["categories"][m[0]]["group"] == "Monsters"], dtype=float)
        radius = cluster * float((points.max(0) - points.min(0)).max())
    for pet_id, markers in candidates.items():
        pet = pets[pet_id]
        if pet["icon"] not in payload["icons"]:
            payload["icons"].append(pet["icon"])
        icon = payload["icons"].index(pet["icon"])
        genus = pet["genus"].capitalize()
        if genus not in categories:
            categories[genus] = len(payload["categories"])
            payload["categories"].append({"name": genus, "group": "Pets", "icon": icon,
                                          "hidden": False, "count": 0})
        category = categories[genus]
        for group in source_groups(markers, radius):
            # The symbol sits on the monster closest to the middle of its spawn group.
            centre = np.mean([m[1:3] for m in group], axis=0)
            marker = min(group, key=lambda m: (m[1] - centre[0]) ** 2 + (m[2] - centre[1]) ** 2)
            source = {"en": marker[3], "de": marker[4]}
            if len(group) > 1:
                source = {"en": f"{marker[3]} ({len(group)}×)", "de": f"{marker[4] or marker[3]} ({len(group)}×)"}
            payload["categories"][category]["count"] += 1
            added.append([category, marker[1], marker[2], pet["en"], pet.get("de"), icon, pet["id"], source])
    if added:
        group = next((g for g in payload["groups"] if g["name"] == "Pets"), None)
        if group is None:
            group = {"name": "Pets", "categories": []}
            payload["groups"].append(group)
        group["categories"].extend(categories.values())
        payload["markers"].extend(added)


def source_groups(markers, radius):
    """Spawn groups: monsters chained closer than radius (single linkage); each its own without radius."""
    if not radius:
        return [[m] for m in markers]
    points = np.array([m[1:3] for m in markers], dtype=float)
    label = [-1] * len(markers)
    groups = []
    for start in range(len(markers)):
        if label[start] >= 0:
            continue
        label[start] = len(groups)
        members, stack = [start], [start]
        while stack:
            near = np.linalg.norm(points - points[stack.pop()], axis=1) <= radius
            for other in np.flatnonzero(near):
                if label[other] < 0:
                    label[other] = len(groups)
                    members.append(int(other))
                    stack.append(int(other))
        groups.append([markers[i] for i in sorted(members)])
    return groups
