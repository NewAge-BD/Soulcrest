const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function fixture() {
    const values = new Map();
    const root = { dataset: {}, style: { setProperty: (name, value) => values.set(name, value) } };
    const window = { getComputedStyle: () => ({ getPropertyValue: name => values.get(name) ?? '' }) };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/app-themes.js'), 'utf8'),
        { window, document: { documentElement: root } });
    return { root, values, apply: window.soulcrestAppTheme.apply, window };
}
const dark = '--bg:#212121;--panel:#171717;--panel-2:#2f2f2f;--line:#424242;--text:#ececec;--muted:#b4b4b4;--accent:#f4f4f4;--accent-ink:#171717;';
const classic = '--bg:#0e1117;--panel:#161b24;--panel-2:#1d2430;--line:#2a3342;--text:#e6ebf2;--muted:#8b96a8;--accent:#5eead4;--accent-ink:#062b27;';

test('switches the main document in both directions without changing semantic or overlay variables', () => {
    const f = fixture();
    f.root.style.setProperty('--ok', '#22c55e');
    f.root.style.setProperty('--overlay-fill', '#123456');
    assert.equal(f.apply('graphite-dark', dark), 'graphite-dark');
    assert.equal(f.root.dataset.appTheme, 'graphite-dark');
    assert.equal(f.values.get('--bg'), '#212121');
    assert.equal(f.apply('soulcrest-classic', classic), 'soulcrest-classic');
    assert.equal(f.values.get('--bg'), '#0e1117');
    assert.equal(f.values.get('--ok'), '#22c55e');
    assert.equal(f.values.get('--overlay-fill'), '#123456');
});

test('rejects incomplete or unknown palettes before altering the displayed theme', () => {
    const f = fixture();
    f.apply('soulcrest-classic', classic);
    for (const [id, palette] of [['missing', dark], ['graphite-dark', '--bg:#212121;'],
        ['graphite-dark', dark.replace('--panel:', '--bg:')], ['graphite-dark', dark.replace('#212121', 'red')]]) {
        assert.equal(f.apply(id, palette), null);
        assert.equal(f.root.dataset.appTheme, 'soulcrest-classic');
        assert.equal(f.values.get('--bg'), '#0e1117');
    }
});

test('does not acknowledge a theme when browser-computed values disagree', () => {
    const f = fixture();
    f.window.getComputedStyle = () => ({ getPropertyValue: () => '' });
    assert.equal(f.apply('graphite-dark', dark), null);
});
