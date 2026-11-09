const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const vm = require('node:vm');

const directory = path.join(__dirname, '../src/Soulcrest.App/wwwroot/overlay-kit');
const read = name => fs.readFileSync(path.join(directory, name), 'utf8');
const manifest = JSON.parse(read('source-manifest.json'));
const hash = text => crypto.createHash('sha256').update(text).digest('hex');
// User request 2026-11-06: the font-shrink fix is an explicit list-only Kit adaptation.
// Reverse only its exact reviewed additions, retaining the immutable original source hash.
const listAdaptations = [
    [`                // List names keep the configured row font. Their full text remains on the item tooltip.
                if (line.classList.contains("overlay-item-name") && line.closest(".overlay-widget-items.is-list")) return;
`, ''],
    [`                // User request 2026-11-06: prevent drop-count-dependent list font shrinking.
                // The server caps lists to complete rows. During a pending resize,
                // clip complete rows at their configured size instead of shrinking the list.
                // Other views retain the original fit of their previous arrangement.
`, `                // During an unequal drag the server's item grid still has its
                // previous arrangement. Fit that complete grid until the new
                // shared layout arrives, retaining every rendered item.
`],
    [`                    if (items.classList.contains("is-list")) {
                        items.style.transform = "none";
                        const rowHeight = parseFloat(style.getPropertyValue("--loot-cell-height"));
                        const gap = Math.max(0, parseFloat(style.getPropertyValue("--loot-gap")) || 0);
                        const measuredHeight = parseFloat(getComputedStyle(viewport).height);
                        const availableHeight = Number.isFinite(measuredHeight) ? measuredHeight : viewport.clientHeight;
                        const rows = rowHeight > 0 ? Math.max(0, Math.floor((availableHeight + gap + .02) / (rowHeight + gap))) : 0;
                        const shownHeight = rows * rowHeight + Math.max(0, rows - 1) * gap;
                        items.style.clipPath = rowHeight > 0 && height > 0
                            ? \`inset(0 0 \${Math.max(0, height - shownHeight)}px 0)\` : "none";
                        return;
                    }
                    items.style.clipPath = "";
`, ''],
];
const normalizeListAdaptation = source => listAdaptations.reduce((text, [added, original]) => text.replace(added, original), source);
// User request 2026-11-09: keep the selection while using the relocated library's controls.
const librarySelectionAdaptation = `                // User request 2026-11-09: browsing modules must keep the current widget selection.
                if (root.contains(e.target) && e.target.closest(".oe-library-controls")) return;
`;
// User request 2026-11-08: native theme frames belong to the canvas; modules stay unboxed.
// Reverse these exact paint-only adaptations before checking the original declarations.
const themeAdaptations = [
    ['.overlay-editor .oe-stage{position:relative;transform-origin:top left;border:0;border-radius:0;background:transparent;box-shadow:none;isolation:isolate}',
        '.overlay-editor .oe-stage{position:relative;transform-origin:top left;border:1px dashed #5c697877;border-radius:9px;box-shadow:0 10px 30px #0003;isolation:isolate}'],
    ['.overlay-editor .oe-stage.has-border{border:0}', '.overlay-editor .oe-stage.has-border{border:1px solid #d0b77499}'],
    ['.overlay-editor .oe-canvas-frame{position:absolute;inset:0;width:100%;height:100%;display:block;pointer-events:none;user-select:none;z-index:0}\n', ''],
    ['.overlay-editor .oe-stage-content,.overlay-editor .oe-preset-stage-content{z-index:1}\n', ''],
    ['.overlay-editor .oe-widget{position:absolute;border:0;border-radius:0;touch-action:none;outline:none;user-select:none}',
        '.overlay-editor .oe-widget{position:absolute;border:1px solid transparent;border-radius:5px;touch-action:none;outline:none;user-select:none}'],
    ['.overlay-editor .oe-widget-content{position:absolute;inset:0;overflow:hidden;border-radius:0;pointer-events:none}',
        '.overlay-editor .oe-widget-content{position:absolute;inset:0;overflow:hidden;border-radius:4px;pointer-events:none}'],
    ['.overlay-editor .oe-widget:hover{outline:1px solid #b5a16d88;outline-offset:-1px}', '.overlay-editor .oe-widget:hover{border-color:#b5a16d88}'],
    ['.overlay-editor .oe-widget.is-selected,.overlay-editor .oe-widget:focus-visible{outline:1px solid var(--gold);outline-offset:-1px;box-shadow:none;z-index:3}',
        '.overlay-editor .oe-widget.is-selected,.overlay-editor .oe-widget:focus-visible{border-color:var(--gold);box-shadow:0 0 0 1px #d6b26a33;z-index:3}'],
];
const leafRules = css => Array.from(css.replace(/\/\*[\s\S]*?\*\//g, '')
    .matchAll(/([^{}]+)\{([^{}]*)\}/g), match => ({ selector: match[1].trim(), declarations: match[2] }));
function groupedRules(css, media = []) {
    css = css.replace(/\/\*[\s\S]*?\*\//g, '');
    const result = [];
    let position = 0;
    while (position < css.length) {
        const opening = css.indexOf('{', position);
        if (opening < 0) break;
        const selector = css.slice(position, opening).trim();
        let ending = opening + 1, depth = 1, quote = null;
        for (; ending < css.length && depth; ending++) {
            const character = css[ending];
            if (quote) {
                if (character === '\\') ending++;
                else if (character === quote) quote = null;
            } else if (character === '"' || character === "'") quote = character;
            else if (character === '{') depth++;
            else if (character === '}') depth--;
        }
        const content = css.slice(opening + 1, ending - 1);
        if (selector.startsWith('@media') || selector.startsWith('@container')) result.push(...groupedRules(content, [...media, selector]));
        else result.push({ selector, media, declarations: content.replace(/\s+/g, ' ').trim() });
        position = ending;
    }
    return result;
}

test('editor and alignment logic match Kit 1.15.0 after only the documented embedding adaptations', () => {
    assert.equal(manifest.sourceRevision, '231a2f0d40dcba872a2a83aa86b8f7b7a1edf49d');
    for (const name of ['overlay-editor.js', 'overlay-alignment.js']) {
        let source = read(name).replace(/^\/\*[^\n]*\*\/\r?\n/, '')
            .replaceAll('soulcrestOverlayEditor', 'grindcrestOverlayEditor')
            .replaceAll('soulcrestOverlayAlignment', 'grindcrestOverlayAlignment')
            .replace('ghost.className = "overlay-editor oe-drag-ghost";', 'ghost.className = "oe-drag-ghost";')
            .replaceAll('\r\n', '\n');
        if (name === 'overlay-editor.js') source = normalizeListAdaptation(source).replace(librarySelectionAdaptation, '');
        assert.equal(hash(source), manifest.files[`src/BdoGrindTracker.App/wwwroot/${name}`].normalizedTextSha256, name);
    }
});

test('the authorized list-font adaptation is present exactly once before reversing it for provenance', () => {
    const source = read('overlay-editor.js').replaceAll('\r\n', '\n');
    for (const [added] of listAdaptations) assert.equal(source.split(added).length - 1, 1, added);
});

test('the authorized library selection adaptation is present exactly once before reversing it for provenance', () => {
    const source = read('overlay-editor.js').replaceAll('\r\n', '\n');
    assert.equal(source.split(librarySelectionAdaptation).length - 1, 1);
});

test('original editor declarations and breakpoints remain unchanged apart from authorized theme and library adaptations', () => {
    let css = read('overlay-editor.css').replaceAll('\r\n', '\n');
    const start = '/* Widget library layout adaptation:begin (user request 2026-11-09). */';
    const end = '/* Widget library layout adaptation:end. */';
    assert.equal(css.split(start).length - 1, 1);
    assert.equal(css.split(end).length - 1, 1);
    css = css.slice(0, css.indexOf(start)) + css.slice(css.indexOf(end) + end.length);
    for (const [added, original] of themeAdaptations) {
        assert.equal(css.split(added).length - 1, 1, added);
        css = css.replace(added, original);
    }
    const original = manifest.files['src/BdoGrindTracker.App/wwwroot/overlay-editor.css'];
    const declarations = leafRules(css).map(rule => rule.declarations.replace(/\s+/g, ' ').trim()).join('\n');
    assert.equal(hash(declarations), original.declarationsSha256);
    assert.deepEqual(Array.from(css.matchAll(/@media[^\{]+/g), match => match[0]), original.mediaQueries);
    const foundation = groupedRules(read('base.css')).filter(rule =>
        rule.selector === '.overlay-editor.page' || rule.selector.startsWith('.overlay-editor .status-dot'));
    assert.deepEqual(foundation, manifest.files['src/BdoGrindTracker.App/wwwroot/app.css'].pageAndStatusRules);
    for (const rule of foundation.filter(rule => rule.selector === '.overlay-editor.page'))
        assert.doesNotMatch(rule.declarations, /\b(?:height|overflow(?:-[xy])?)\s*:/);
});

for (const name of ['base.css', 'overlay-editor.css', 'overlay-widgets.css', 'browser-preview.css']) {
    test(`${name} cannot select application content outside its editor boundary`, () => {
        const css = read(name);
        const rules = groupedRules(css);
        assert.ok(rules.length > 0);
        for (const rule of rules) {
            if (rule.selector.startsWith('@keyframes ')) {
                assert.match(rule.selector, /^@keyframes oe-(?:rarity|daily|abyss)-[a-z-]+$/);
                continue; // Keyframe steps animate paint; they do not select DOM content.
            }
            // Commas inside :has() and :not() belong to a selector, not a new boundary.
            const selectors = rule.selector.split(/,(?![^()]*\))/);
            for (const selector of selectors)
                assert.match(selector.trim(), /\.overlay-editor\b/, selector);
        }
        assert.doesNotMatch(css, /url\s*\(/i);
        assert.doesNotMatch(css, /(?:^|[{},])\s*(?:html|body|:root|#app)\s*(?:[,{])/m);
    });
}

test('neutral metric typography is original and includes labels, icons, tones and fit hints', () => {
    const css = read('overlay-widgets.css');
    for (const selector of ['.overlay-widget-viewport', '.overlay-widget-label', '.overlay-widget-value',
        '.overlay-widget-detail', '.overlay-value-text', '.metric-tone-muted', '.metric-tone-positive', '.metric-tone-accent'])
        assert.ok(css.includes(selector), selector);
    assert.match(css, /font-size: calc\(22px \* var\(--widget-font-scale, 1\) \* var\(--line-fit, 1\)\)/);
    assert.doesNotMatch(css, /overlay-widget-(?:silver|trash|spot|loot-scroll|grind-rating|controls)/);
});

test('native dialog styling keeps its own boundary and does not inherit the editor page inset', () => {
    const css = read('base.css') + read('overlay-editor.css') + read('browser-preview.css');
    assert.match(css, /\.overlay-editor\.modal::backdrop/);
    assert.match(css, /\.overlay-editor\.browser-overlay-dialog::backdrop/);
    assert.match(css, /\.overlay-editor:not\(dialog\):not\(\.oe-drag-ghost\)\{padding-top:22px\}/);
    assert.doesNotMatch(css, /\.overlay-editor\{height:100%;margin:0;overflow:hidden\}/);
    assert.doesNotMatch(css, /background-scene|spot-backgrounds/);
});

test('isolated dialog helper leaves the existing Soulcrest application API untouched', () => {
    const existing = { marker: 'existing application' };
    const window = { soulcrest: existing };
    vm.runInNewContext(read('dialogs.js'), { window, document: {} });
    assert.equal(window.soulcrest, existing);
    assert.deepEqual(Object.keys(window.soulcrestOverlayDialogs), ['showDialog', 'closeDialog']);
});
