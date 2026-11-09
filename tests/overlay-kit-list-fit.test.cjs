const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const script = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/overlay-kit/overlay-editor.js'), 'utf8');

const style = values => Object.assign({ setProperty(name, value) { this[name] = String(value); } }, values);
const classes = names => ({ contains(name) { return names().includes(name); }, remove() {} });

function setup({ mode = 'list', rows = 10, rowHeight = 28, staleTransform = '' } = {}) {
    let view = mode, count = rows, nextFrame = 0, computedViewportHeight;
    const frames = new Map(), observers = [];
    const content = { style: style({}) };
    const items = { style: style({ transform: staleTransform }), classList: classes(() => [`is-${view}`]) };
    const lootViewport = {
        get clientWidth() { return parseFloat(content.style.width) - 20; },
        get clientHeight() { return parseFloat(content.style.height) - 16 - 20 - 20; },
        querySelector(selector) { return selector === '.overlay-widget-items' ? items : null; }
    };
    function line(name) {
        return {
            name, style: style({ '--line-fit': '.03' }), measurements: 0,
            title: name === 'name' ? 'The complete original item name' : '1234567890123456789',
            classList: classes(() => name === 'name' ? ['overlay-item-name'] : ['overlay-item-quantity']),
            closest(selector) { return selector === '.overlay-widget-items.is-list' && view === 'list' ? items : null; },
            getClientRects() { this.measurements++; return [{}]; },
            getBoundingClientRect() { return { left: 0, top: 0, right: 80, bottom: 20, width: 80, height: 20 }; },
            querySelectorAll() { return []; }
        };
    }
    const name = line('name'), quantity = line('quantity');
    const widgetViewport = {
        dataset: { reflow: 'false', contentWidth: '200', contentHeight: '160', minContentWidth: '80', minContentHeight: '48' },
        querySelector(selector) { return selector === '.overlay-widget-preview' ? content : null; }
    };
    const widget = {
        dataset: { width: '200', height: '160' },
        querySelector(selector) { return selector === '.overlay-widget-viewport' ? widgetViewport : null; }
    };
    const viewport = { clientWidth: 808 }, wrap = { style: {} };
    const stage = {
        dataset: { overlayId: 'list-window', width: '400', height: '300' }, style: {}, classList: classes(() => []),
        querySelector() { return null; },
        querySelectorAll(selector) {
            return selector === '.oe-widget' ? [widget] : selector === '.overlay-loot-viewport' ? [lootViewport]
                : selector === '[data-overlay-fit]' ? [name, quantity] : [];
        }
    };
    const root = {
        querySelector(selector) { return { '.oe-stage-viewport': viewport, '.oe-stage-wrap': wrap, '.oe-stage': stage }[selector] ?? null; },
        removeEventListener() {}
    };
    let selectedLine;
    const document = {
        getElementById() { return root; }, body: { classList: classes(() => []) }, removeEventListener() {},
        createRange() {
            return { selectNodeContents(value) { selectedLine = value; },
                getClientRects() { assert.ok(selectedLine); return [{ left: 0, top: 0, right: 600, bottom: 10, width: 600, height: 10 }]; } };
        }
    };
    class Observer {
        constructor(callback) { this.callback = callback; observers.push(this); }
        observe() {} disconnect() {}
    }
    const window = {};
    vm.runInNewContext(script, {
        window, document, ResizeObserver: Observer, MutationObserver: Observer,
        requestAnimationFrame(callback) { const id = ++nextFrame; frames.set(id, callback); return id; },
        cancelAnimationFrame(id) { frames.delete(id); },
        getComputedStyle(element) {
            return {
                display: 'block', clipPath: 'none', width: '80', height: element === lootViewport ? computedViewportHeight : undefined,
                paddingLeft: '0', paddingRight: '0', paddingTop: '0', paddingBottom: '0',
                borderLeftWidth: '0', borderRightWidth: '0', borderTopWidth: '0', borderBottomWidth: '0',
                getPropertyValue(key) {
                    assert.equal(element, items);
                    return { '--loot-layout-width': '180', '--loot-layout-height': String(count * rowHeight + Math.max(0, count - 1) * 4),
                        '--loot-cell-height': String(rowHeight), '--loot-gap': '4' }[key] ?? '';
                }
            };
        }
    });
    const flush = () => {
        while (frames.size) { const [id, callback] = frames.entries().next().value; frames.delete(id); callback(); }
    };
    window.soulcrestOverlayEditor.mount('editor', null, true);
    flush();
    return { items, content, name, quantity, widget, lootViewport,
        mutateRows(value) { count = value; observers[1].callback([{ type: 'childList', target: items }]); flush(); },
        resize(width, height) { widget.dataset.width = String(width); widget.dataset.height = String(height); observers[0].callback(); flush(); },
        measureViewport(height) {
            computedViewportHeight = String(height);
            Object.defineProperty(lootViewport, 'clientHeight', { value: Math.round(height), configurable: true });
            observers[0].callback(); flush();
        },
        changeView(value) { view = value; observers[1].callback([{ type: 'attributes', target: items }]); flush(); }
    };
}

test('additional session rows never shrink the whole list after a snapshot mutation', () => {
    const editor = setup({ rows: 3 });
    for (const count of [3, 10, 50, 200]) {
        editor.mutateRows(count);
        assert.equal(editor.items.style.transform, 'none');
        assert.equal(editor.content.style.transform, 'scale(1)');
        assert.equal(editor.name.style['--line-fit'], '1');
    }
});

test('a stale list transform from an earlier grid or fitter is removed on mount and the next snapshot', () => {
    const editor = setup({ staleTransform: 'scale(0.02)' });
    assert.equal(editor.items.style.transform, 'none');
    editor.items.style.transform = 'scale(0.07)';
    editor.mutateRows(40);
    assert.equal(editor.items.style.transform, 'none');
});

test('list item names keep their font and complete tooltip while long exact quantities still fit', () => {
    const editor = setup();
    assert.equal(editor.name.measurements, 0);
    assert.equal(editor.name.style['--line-fit'], '1');
    assert.equal(editor.name.title, 'The complete original item name');
    assert.ok(editor.quantity.measurements > 0);
    assert.ok(Number(editor.quantity.style['--line-fit']) < 1);
    assert.equal(editor.quantity.title, '1234567890123456789');
});

test('a pending vertical resize reveals only complete fixed rows instead of scaling the list', () => {
    const editor = setup();
    // 104px of row viewport holds three 28px rows with two 4px gaps = 92px.
    assert.equal(editor.items.style.clipPath, 'inset(0 0 224px 0)');
    editor.resize(200, 240);
    // 184px holds five complete rows = 156px; the 28px partial row remains clipped.
    assert.equal(editor.items.style.clipPath, 'inset(0 0 160px 0)');
    assert.equal(editor.items.style.transform, 'none');
    assert.equal(editor.content.style.transform, 'scale(1)');
    // A row viewport with less than one complete row exposes no partial text.
    Object.defineProperty(editor.lootViewport, 'clientHeight', { value: 20 });
    editor.mutateRows(10);
    assert.equal(editor.items.style.clipPath, 'inset(0 0 316px 0)');
});

test('an intentional uniform module resize still scales its content once', () => {
    const editor = setup();
    editor.resize(400, 320);
    assert.equal(editor.content.style.transform, 'scale(2)');
    assert.equal(editor.items.style.transform, 'none');
    assert.equal(editor.items.style.clipPath, 'inset(0 0 224px 0)');
});

test('fractional row boundaries use CSS height and only a subpixel tolerance instead of rounded clientHeight', () => {
    const editor = setup({ rows: 8, rowHeight: 30.8 });
    editor.measureViewport(274.4);
    assert.equal(editor.lootViewport.clientHeight, 274);
    assert.equal(editor.items.style.clipPath, 'inset(0 0 0px 0)');
    editor.measureViewport(274.39);
    assert.equal(editor.items.style.clipPath, 'inset(0 0 0px 0)');
    editor.measureViewport(274.37);
    const clipped = Number(editor.items.style.clipPath.match(/inset\(0 0 ([\d.]+)px 0\)/)[1]);
    assert.ok(Math.abs(clipped - 34.8) < 1e-6, 'a genuinely incomplete eighth row must remain clipped');
});

for (const mode of ['grid', 'strip', 'card']) {
    test(`${mode} keeps the original container and name fitting, including after leaving list view`, () => {
        const editor = setup({ mode });
        assert.match(editor.items.style.transform, /^scale\(/);
        assert.equal(editor.items.style.clipPath, '');
        assert.ok(editor.name.measurements > 0);
        assert.ok(Number(editor.name.style['--line-fit']) < 1);
        editor.changeView('list');
        assert.equal(editor.items.style.transform, 'none');
        assert.equal(editor.name.style['--line-fit'], '1');
        editor.changeView(mode);
        assert.match(editor.items.style.transform, /^scale\(/);
        assert.equal(editor.items.style.clipPath, '');
    });
}
