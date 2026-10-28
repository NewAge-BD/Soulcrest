"""Browser-Vorschau der Karte ohne Desktop-App (Entwicklungswerkzeug).

Liefert src/Soulcrest.App/wwwroot unter / und das Kartendatenpaket unter /mapdata/ aus und
bietet /preview.html mit derselben soulcrest-map.js wie die App.

  python scripts/preview_map.py [port]   ->  http://127.0.0.1:5191/preview.html?map=altgard
"""
import functools
import http.server
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WWWROOT = os.path.join(ROOT, "src", "Soulcrest.App", "wwwroot")
MAPDATA = os.environ.get("SOULCREST_MAPDATA", os.path.join(ROOT, "imports", "generated", "mapdata"))

PREVIEW = """<!DOCTYPE html><html lang="de"><head><meta charset="utf-8"><title>Soulcrest Kartenvorschau</title>
<link rel="stylesheet" href="lib/leaflet/leaflet.css"><link rel="stylesheet" href="css/app.css"><link rel="stylesheet" href="css/cube-groups.css"><link rel="stylesheet" href="css/hunting-grounds.css">
<style>body{height:100vh} #map{position:absolute;inset:40px 0 0 0} #bar{height:40px;display:flex;gap:.5rem;align-items:center;padding:0 .75rem;background:#161b24}</style>
</head><body><div id="bar"><b style="color:#5eead4">Kartenvorschau</b><span id="info" class="note"></span><label class="check"><input id="show-cubes" type="checkbox"> Hidden Cubes</label><label class="check"><input id="show-cube-groups" type="checkbox"> Cube Groups</label></div><div id="map"></div>
<script src="lib/leaflet/leaflet.js"></script><script src="js/soulcrest-cube-groups.js"></script><script src="js/soulcrest-canvas-icons.js"></script><script src="js/soulcrest-map.js"></script>
<script>
const params = new URLSearchParams(location.search);
const fake = { invokeMethodAsync: (...a) => { console.log('dotnet', ...a); return Promise.resolve(); } };
(async () => {
  const manifest = await (await fetch('/mapdata/manifest.json')).text();
  soulcrestMap.init('map', manifest, '/mapdata/', fake);
  const info = JSON.parse(await soulcrestMap.show(params.get('map') || 'altgard', 'de', '[]'));
  document.getElementById('info').textContent = `${info.markers} Marker, ${info.regions} Gebiete`;
  const categories = info.groups.flatMap(g => g.categories.map(c => ({ ...c, group: g.name })));
  const selected = new Set((params.get('only') ?? params.get('show') ?? '').split(',').filter(Boolean));
  for (const c of categories) {
    const chosen = selected.has(c.name) || selected.has(c.group);
    if (params.has('only') || chosen) {
      soulcrestMap.setCategoryVisible(c.index, chosen);
      c.hidden = !chosen;
    }
  }
  for (const [id, name] of [['show-cubes', 'Hidden Cube'], ['show-cube-groups', 'Cube Groups']]) {
    const checkbox = document.getElementById(id);
    const category = categories.find(c => c.group === 'Collectibles' && c.name === name);
    checkbox.disabled = !category;
    checkbox.checked = category && !category.hidden;
    checkbox.addEventListener('change', () => soulcrestMap.setCategoryVisible(category.index, checkbox.checked));
  }
  const payload = window.SoulcrestMaps[params.get('map') || 'altgard'];
  const group = (payload.hiddenCubeGroups || []).find(g => g.number === Number(params.get('cubeGroup')));
  if (group) {
    const scale = Math.pow(2, payload.refZoom);
    const points = group.markers.map(index => [-payload.markers[index][2] / scale, payload.markers[index][1] / scale]);
    soulcrestMap.debugState().map.fitBounds(L.latLngBounds(points).pad(0.2));
  }
  if (params.get('pet')) soulcrestMap.focusPet(params.get('pet'));
  window.previewInfo = info;
})();
</script></body></html>"""


class Handler(http.server.SimpleHTTPRequestHandler):
    def translate_path(self, path):
        clean = path.split("?")[0].split("#")[0]
        if clean.startswith("/mapdata/"):
            return os.path.join(MAPDATA, *clean[len("/mapdata/"):].split("/"))
        return os.path.join(WWWROOT, *clean.lstrip("/").split("/"))

    def do_GET(self):
        if self.path.split("?")[0] == "/preview.html":
            body = PREVIEW.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        super().do_GET()

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 5191
    print(f"http://127.0.0.1:{port}/preview.html?map=altgard")
    http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
