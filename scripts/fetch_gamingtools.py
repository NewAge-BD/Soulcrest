"""Cache the static gaming.tools files for AION 2: the .d.json data files plus the tile packs and images
they point to, all from cdn-hosted.gaming.tools.

Permission (Discord, 2026-10-06, Adain from gaming.tools): use the d.json files instead of scraping the
web, with a visible, clickable "aion2.gaming.tools" link in the app and on a later website. The tile
packs, marker icons and pet portraits the files reference were confirmed as fine as well. Website pages
(aion2.gaming.tools) are not fetched. No cookies, credentials, internal APIs or query strings.
"""
import argparse
import json
from pathlib import Path
import re
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT / 'imports/gamingtools/raw'
CDN = 'https://cdn-hosted.gaming.tools/aion2'
USER_AGENT = 'Soulcrest-Map-Importer/1.1 (d.json files with permission; cached; max 1 request/second)'


def decode(values):
    if not isinstance(values, list):
        return values
    memo = {}
    def resolve(index):
        if index < 0:
            return None
        if index in memo:
            return memo[index]
        value = values[index]
        if isinstance(value, list):
            result = []
            memo[index] = result
            result.extend(resolve(v) for v in value)
        elif isinstance(value, dict):
            result = {}
            memo[index] = result
            result.update((k, resolve(v)) for k, v in value.items())
        else:
            result = value
        return result
    return resolve(0)


def static_path(path):
    """A CDN path below /data, /tiles or /images, without traversal or query."""
    if not re.fullmatch(r'/(?:data|tiles|images)/[A-Za-z0-9_./-]+', path) or '..' in path.split('/'):
        raise ValueError('Unexpected static CDN path: ' + path)
    return path


class PublicCache:
    def __init__(self):
        CACHE.mkdir(parents=True, exist_ok=True)
        self.last_request = 0
        self.blocked = False

    def request(self, path):
        """One throttled GET on the CDN; redirects are not followed, 403/429 stops all further requests."""
        if self.blocked:
            raise RuntimeError('Fetching stopped after HTTP 403/429.')
        static_path(path)
        time.sleep(max(0, 1.05 - (time.monotonic() - self.last_request)))
        self.last_request = time.monotonic()
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *args, **kwargs):
                return None
        try:
            with urllib.request.build_opener(NoRedirect).open(
                    urllib.request.Request(CDN + path, headers={'User-Agent': USER_AGENT}), timeout=60) as response:
                return response.read()
        except urllib.error.HTTPError as error:
            if error.code in (403, 429):
                self.blocked = True
            raise

    def _store(self, target, path, check=None):
        if target.exists():
            return target
        content = self.request(path)
        if check is not None:
            check(content)
        target.parent.mkdir(parents=True, exist_ok=True)
        temporary = target.with_suffix('.tmp')
        temporary.write_bytes(content)
        temporary.replace(target)
        return target

    def cdn(self, path):
        """A static file (data, tile pack); cached under imports/gamingtools/cdn."""
        return self._store(CACHE.parent / 'cdn' / static_path(path).lstrip('/'), path)

    def data(self, kind, entity_id=None, locale='en'):
        path = '/data/' + locale + '/' + kind + ('/' + str(entity_id) if entity_id else '') + '.d.json'
        raw = self.cdn(path)
        decoded_path = raw.with_name(raw.name.replace('.d.json', '.json'))
        if decoded_path.exists():
            return json.loads(decoded_path.read_text(encoding='utf-8'))
        result = decode(json.loads(raw.read_text(encoding='utf-8')))
        decoded_path.write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding='utf-8')
        return result

    def image(self, path):
        """A marker icon or pet portrait (WebP) the data files point to; cached under imports/gamingtools/assets."""
        if not re.fullmatch(r'/images/[A-Za-z0-9_./-]+\.webp', path) or '..' in path.split('/'):
            raise ValueError('Expected a WebP image path: ' + path)
        def is_webp(content):
            if content[:4] != b'RIFF' or content[8:12] != b'WEBP':
                raise ValueError('Response is not WebP: ' + path)
        return self._store(CACHE.parent / 'assets' / path.lstrip('/'), path, is_webp)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('kinds', nargs='+', help='Data files, e.g. pets interactive-map interactive-map/1110')
    parser.add_argument('--locale', default='en')
    args = parser.parse_args()
    cache = PublicCache()
    for kind in args.kinds:
        name, _, entity = kind.partition('/')
        data = cache.data(name, entity or None, args.locale)
        print(kind, type(data).__name__, len(data), flush=True)
