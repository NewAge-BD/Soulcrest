// Ported from overlay-editor-kit 1.15.0; original test scenarios preserved.
// Run with: node --test tests/overlay-kit-interactions.test.cjs
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const script = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/overlay-kit/overlay-editor.js'), 'utf8');
const alignmentScript = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/overlay-kit/overlay-alignment.js'), 'utf8');

function classList() {
    const values = new Set();
    return {
        add(...names) { names.forEach(name => values.add(name)); },
        remove(...names) { names.forEach(name => values.delete(name)); },
        toggle(name, enabled) { if (enabled) values.add(name); else values.delete(name); },
        contains(name) { return values.has(name); }
    };
}

function setup({ chrome = true, snap = false, autoAlign = false, corner = 'se', selected = false, widgets: widgetLayouts = [] } = {}) {
    const width = 400, height = 200;
    const chromeX = chrome ? 4 : 0, chromeY = chrome ? 34 : 0;
    const chromeLeft = chrome ? 2 : 0, chromeTop = chrome ? 32 : 0;
    const listeners = new Map(), calls = [], observers = [];
    const guideLayer = { children: [], style: { setProperty(name, value) { this[name] = value; } },
        replaceChildren() { this.children = []; }, appendChild(element) { this.children.push(element); } };
    const stage = {
        dataset: { overlayId: 'overlay-1', width: String(width), height: String(height), snap: String(snap), autoAlign: String(autoAlign),
            ...(chrome ? { chromeX: String(chromeX), chromeY: String(chromeY) } : {}) },
        style: { width: `${width + chromeX}px`, height: `${height + chromeY}px` }, classList: classList(),
        querySelector(selector) { return selector === '.oe-stage-content' ? content : selector === '.oe-alignment-guides' ? guideLayer
            : selector === '.oe-widget.is-selected' ? widgets.find(widget => widget.classList.contains('is-selected')) ?? null : null; },
        querySelectorAll(selector) { return selector === '.oe-widget' ? widgets : []; },
        contains(element) { return widgets.includes(element); },
        getBoundingClientRect() {
            const scale = Number(this.style.transform?.match(/scale\(([^)]+)\)/)?.[1] ?? 1);
            const width = parseFloat(this.style.width) * scale, height = parseFloat(this.style.height) * scale;
            const { left, top } = wrap.getBoundingClientRect();
            return { left, top, right: left + width, bottom: top + height, width, height, scale };
        }
    };
    const content = {
        getBoundingClientRect() {
            const rect = stage.getBoundingClientRect();
            const left = rect.left + chromeLeft * rect.scale, top = rect.top + chromeTop * rect.scale;
            return { left, top, right: left + (parseFloat(stage.style.width) - chromeX) * rect.scale,
                bottom: top + (parseFloat(stage.style.height) - chromeY) * rect.scale };
        }
    };
    const wrap = {
        style: {},
        getBoundingClientRect() {
            const width = parseFloat(this.style.width), height = parseFloat(this.style.height);
            // CSS margin: 0 auto recenters the wrapper whenever its layout
            // width changes; relative left/top then apply the drag correction.
            const left = 96 + Math.max(0, (viewport.clientWidth - width) / 2) + (parseFloat(this.style.left) || 0);
            const top = 50 + (parseFloat(this.style.top) || 0);
            return { left, top, right: left + width, bottom: top + height, width, height };
        }
    };
    // Exactly half-size on screen: fit must include both side borders.
    const viewport = { clientWidth: (width + chromeX) / 2 + 8 };
    const capture = selector => ({
        dataset: {}, disabled: false, captured: new Set(),
        closest(current) { return current === selector ? this : null; },
        matches() { return false; }, focus() {}, querySelector() { return null; },
        setPointerCapture(id) { this.captured.add(id); },
        hasPointerCapture(id) { return this.captured.has(id); },
        releasePointerCapture(id) { this.captured.delete(id); }
    });
    const module = capture('[data-module-kind]');
    module.dataset.moduleKind = 'duration';
    module.dataset.moduleWidth = '160'; module.dataset.moduleHeight = '80';
    module.querySelector = selector => selector === 'strong' ? { textContent: 'Zeit' } : null;
    const libraryControls = {};
    const librarySearch = capture('input[type=search]'), libraryCategory = capture('button'), libraryCategoryIcon = capture('svg');
    for (const control of [librarySearch, libraryCategory, libraryCategoryIcon])
        control.closest = selector => selector === '.oe-library-controls' ? libraryControls : null;
    const canvasHandles = Object.fromEntries(['nw', 'ne', 'sw', 'se'].map(corner => {
        const handle = capture('[data-canvas-resize]');
        handle.dataset.canvasResize = corner;
        return [corner, handle];
    }));
    const canvasHandle = canvasHandles[corner];
    const widgets = widgetLayouts.map((layout, index) => ({
        dataset: { widgetId: `widget-${index}`, ...Object.fromEntries(Object.entries(layout).map(([name, value]) => [name, String(value)])) },
        style: { left: `${layout.x}px`, top: `${layout.y}px`, width: `${layout.width}px`, height: `${layout.height}px` }, classList: classList(),
        querySelector() { return null; }, focus() {}, matches() { return false; },
        closest(selector) { return selector === '.oe-widget' ? this : null; },
        getBoundingClientRect() {
            const origin = content.getBoundingClientRect(), scale = stage.getBoundingClientRect().scale;
            const left = origin.left + parseFloat(this.style.left) * scale, top = origin.top + parseFloat(this.style.top) * scale;
            const width = parseFloat(this.style.width) * scale, height = parseFloat(this.style.height) * scale;
            return { left, top, right: left + width, bottom: top + height, width, height };
        }
    }));
    widgets.forEach(widget => {
        for (const [property, selector] of [['grip', '[data-widget-drag]'], ['resize', '[data-widget-resize]']]) {
            widget[property] = capture(selector);
            widget[property].closest = current => current === selector ? widget[property] : current === '.oe-widget' ? widget : null;
        }
    });
    if (selected && widgets.length) widgets[0].classList.add('is-selected');
    const root = {
        querySelector(selector) { return { '.oe-stage-viewport': viewport, '.oe-stage-wrap': wrap, '.oe-stage': stage }[selector] ?? null; },
        contains(element) { return element === module || [libraryControls, librarySearch, libraryCategory, libraryCategoryIcon].includes(element) || Object.values(canvasHandles).includes(element)
            || widgets.some(widget => element === widget || element === widget.grip || element === widget.resize); },
        addEventListener(name, callback) { listeners.set(name, callback); },
        removeEventListener(name, callback) { if (listeners.get(name) === callback) listeners.delete(name); }
    };
    const ghosts = [];
    const document = {
        getElementById() { return root; },
        body: { classList: classList(), appendChild(element) { ghosts.push(element); } },
        createElement() { return { style: {}, children: [], appendChild(element) { this.children.push(element); }, remove() { this.removed = true; } }; },
        listeners: new Map(),
        addEventListener(name, handler) { this.listeners.set(name, handler); },
        removeEventListener(name, handler) { if (this.listeners.get(name) === handler) this.listeners.delete(name); }
    };
    class Observer {
        constructor(callback) { this.callback = callback; observers.push(this); }
        observe() {} disconnect() {}
    }
    const window = {};
    vm.runInNewContext(alignmentScript, { window });
    vm.runInNewContext(script, { window, document, console, ResizeObserver: Observer, MutationObserver: Observer,
        getComputedStyle() { return { paddingLeft: '0', paddingRight: '0' }; },
        requestAnimationFrame() { return 1; }, cancelAnimationFrame() {}, setTimeout() {} });
    window.soulcrestOverlayEditor.mount('editor', { invokeMethodAsync(...args) { calls.push(args); return Promise.resolve(); } });
    const dispatch = (type, values = {}) => {
        const event = { type, button: 0, pointerId: 1, clientX: 0, clientY: 0, target: canvasHandle,
            defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, ...values };
        listeners.get(type)?.(event);
        return event;
    };
    return { api: window.soulcrestOverlayEditor, stage, content, wrap, module, canvasHandle, canvasHandles, widgets, dispatch, calls, ghosts, observers, document, guideLayer,
        librarySearch, libraryCategory, libraryCategoryIcon };
}

test('search and category controls preserve the selected widget while actual outside clicks still clear it', () => {
    const fixture = setup({ selected: true, widgets: [{ x: 20, y: 30, width: 80, height: 40 }] });
    const click = target => fixture.document.listeners.get('click')({ target });
    const geometry = { ...fixture.widgets[0].style };
    for (const target of [fixture.librarySearch, fixture.libraryCategory, fixture.libraryCategoryIcon, fixture.module]) click(target);
    assert.deepEqual(fixture.calls, []);
    assert.deepEqual(fixture.widgets[0].style, geometry);
    // A control from another editor must not borrow this editor's selection protection.
    click({ closest: selector => selector === '.oe-library-controls' ? {} : null });
    assert.deepEqual(fixture.calls, [['ClearSelection']]);
    click({ closest: () => null });
    assert.deepEqual(fixture.calls.at(-1), ['ClearSelection']);
    assert.equal(fixture.calls.length, 2);
});

test('a selected widget does not interfere with module drag and drop after library filtering', () => {
    const { module, dispatch, calls, content, document } = setup({ selected: true, widgets: [{ x: 20, y: 30, width: 80, height: 40 }] });
    document.listeners.get('click')({ target: module });
    const rect = content.getBoundingClientRect();
    dispatch('pointerdown', { target: module });
    dispatch('pointermove', { clientX: rect.left + 21, clientY: rect.top + 36 });
    dispatch('pointerup', { clientX: rect.left + 21, clientY: rect.top + 36 });
    assert.deepEqual(calls, [['AddModuleAt', 'duration', 42, 72]]);
});

test('Enter and Space select a focused widget group without moving it', () => {
    const { widgets, dispatch, calls } = setup({ widgets: [{ x: 20, y: 30, width: 80, height: 40 }] });
    const widget = widgets[0];
    const original = { ...widget.style };
    for (const key of ['Enter', ' ']) {
        assert.equal(dispatch('keydown', { target: widget, key }).defaultPrevented, true);
        assert.deepEqual(calls.at(-1), ['SelectWidget', 'widget-0']);
    }
    assert.deepEqual(widget.style, original);
    assert.equal(calls.some(call => call[0] === 'NudgeWidget'), false);
});

test('widget selection shortcuts leave Tab, modifiers and nested buttons to their normal handlers', () => {
    const { widgets, dispatch, calls } = setup({ widgets: [{ x: 20, y: 30, width: 80, height: 40 }] });
    const widget = widgets[0];
    assert.equal(dispatch('keydown', { target: widget, key: 'Tab' }).defaultPrevented, false);
    assert.equal(dispatch('keydown', { target: widget, key: 'Enter', ctrlKey: true }).defaultPrevented, false);
    assert.equal(dispatch('keydown', { target: widget.grip, key: 'Enter' }).defaultPrevented, false);
    assert.equal(calls.length, 0);
});

test('window fit includes external chrome while saved canvas dimensions remain content only', () => {
    const { stage, wrap } = setup();
    assert.equal(stage.style.transform, 'scale(0.5)');
    assert.equal(wrap.style.width, '202px');
    assert.equal(wrap.style.height, '117px');
    assert.equal(stage.dataset.width, '400');
    assert.equal(stage.dataset.height, '200');
});

test('dropping a module uses the content origin below the title bar at the current scale', () => {
    const { module, dispatch, calls, content, ghosts } = setup();
    const rect = content.getBoundingClientRect();
    dispatch('pointerdown', { target: module, clientX: 0, clientY: 0 });
    dispatch('pointermove', { clientX: rect.left + 21, clientY: rect.top + 36 });
    dispatch('pointerup', { clientX: rect.left + 21, clientY: rect.top + 36 });
    assert.deepEqual(calls, [['AddModuleAt', 'duration', 42, 72]]);
    assert.equal(module.captured.size, 0);
    assert.equal(ghosts[0].removed, true);
});

test('window title bar and borders cannot receive modules as if they were content', () => {
    for (const point of [{ clientX: 150, clientY: 58 }, { clientX: 100.5, clientY: 100 }]) {
        const { module, stage, dispatch, calls } = setup();
        dispatch('pointerdown', { target: module });
        dispatch('pointermove', point);
        assert.equal(stage.classList.contains('is-drop-target'), false);
        dispatch('pointerup', point);
        assert.deepEqual(calls, []);
    }
});

test('canvas resize commits content dimensions and renders outer dimensions including chrome', () => {
    const { stage, wrap, dispatch, calls } = setup();
    dispatch('pointerdown', { clientX: 302, clientY: 167 });
    dispatch('pointermove', { clientX: 352, clientY: 187 });
    assert.equal(stage.style.width, '504px');
    assert.equal(stage.style.height, '274px');
    assert.equal(wrap.style.width, '252px');
    assert.equal(wrap.style.height, '137px');
    // The server remains the owner of committed data attributes.
    assert.equal(stage.dataset.width, '400');
    assert.equal(stage.dataset.height, '200');
    dispatch('pointerup', { clientX: 352, clientY: 187 });
    assert.deepEqual(calls, [['CommitCanvasCorner', 500, 240, 'se']]);
});

for (const cancellation of ['pointercancel', 'Escape']) {
    test(`${cancellation} restores original outer geometry without committing a resized canvas`, () => {
        const { stage, wrap, dispatch, calls, canvasHandle, document } = setup();
        dispatch('pointerdown', { clientX: 302, clientY: 167 });
        dispatch('pointermove', { clientX: 352, clientY: 187 });
        if (cancellation === 'Escape') dispatch('keydown', { key: 'Escape' });
        else dispatch('pointercancel');
        assert.equal(stage.style.width, '404px');
        assert.equal(stage.style.height, '234px');
        assert.equal(wrap.style.width, '202px');
        assert.equal(wrap.style.height, '117px');
        assert.equal(canvasHandle.captured.size, 0);
        assert.equal(document.body.classList.contains('oe-resizing'), false);
        assert.deepEqual(calls, []);
    });
}

test('the default theme without chrome keeps its original drop and resize coordinate system', () => {
    const { stage, wrap, module, dispatch, calls } = setup({ chrome: false });
    assert.equal(stage.style.transform, 'scale(0.5)');
    assert.equal(wrap.style.width, '200px');
    assert.equal(wrap.style.height, '100px');
    dispatch('pointerdown', { target: module });
    dispatch('pointermove', { clientX: 121, clientY: 86 });
    dispatch('pointerup', { clientX: 121, clientY: 86 });
    assert.deepEqual(calls, [['AddModuleAt', 'duration', 42, 72]]);
    dispatch('pointerdown', { clientX: 300, clientY: 150 });
    dispatch('pointermove', { clientX: 350, clientY: 170 });
    assert.equal(stage.style.width, '500px');
    assert.equal(stage.style.height, '240px');
    dispatch('pointerup', { clientX: 350, clientY: 170 });
    assert.deepEqual(calls[1], ['CommitCanvasCorner', 500, 240, 'se']);
});

test('canvas resize applies snapping and limits to content rather than title bar dimensions', () => {
    const { stage, dispatch, calls } = setup({ snap: true });
    dispatch('pointerdown', { clientX: 302, clientY: 167 });
    dispatch('pointermove', { clientX: 309, clientY: 174 });
    assert.equal(stage.style.width, '420px');
    assert.equal(stage.style.height, '250px');
    dispatch('pointermove', { clientX: -1000, clientY: -1000 });
    assert.equal(stage.style.width, '164px');
    assert.equal(stage.style.height, '98px');
    dispatch('pointerup', { clientX: -1000, clientY: -1000 });
    assert.deepEqual(calls, [['CommitCanvasCorner', 160, 64, 'se']]);
});

const spacedWidgets = [
    { x: 20, y: 24, width: 120, height: 64 },
    { x: 180, y: 112, width: 180, height: 72 }
];

const requiredControl = { widgetId: 'overlay-lock', requiredWidget: true, x: 0, y: 0, width: 28, height: 28 };

for (const corner of ['nw', 'ne', 'sw']) {
    test(`${corner} can shrink an otherwise empty canvas with its mandatory control to 160 by 64`, () => {
        const { stage, dispatch, calls, widgets } = setup({ corner, widgets: [requiredControl] });
        const controlStyle = { ...widgets[0].style }, rect = stage.getBoundingClientRect();
        const west = corner.includes('w'), north = corner.includes('n');
        const start = { clientX: west ? rect.left : rect.right, clientY: north ? rect.top : rect.bottom };
        dispatch('pointerdown', start);
        dispatch('pointermove', { clientX: start.clientX + (west ? 500 : -500), clientY: start.clientY + (north ? 500 : -500) });
        assert.equal(stage.style.width, '164px');
        assert.equal(stage.style.height, '98px');
        // The server clamps this separately stored control after committing the new viewport.
        assert.deepEqual(widgets[0].style, controlStyle);
        dispatch('pointerup');
        assert.deepEqual(calls, [['CommitCanvasCorner', 160, 64, corner]]);
    });

    test(`${corner} ignores the mandatory control when measuring and translating real content`, () => {
        const { stage, dispatch, calls, widgets } = setup({ corner, widgets: [requiredControl, ...spacedWidgets] });
        const controlStyle = { ...widgets[0].style }, rect = stage.getBoundingClientRect();
        const west = corner.includes('w'), north = corner.includes('n');
        const start = { clientX: west ? rect.left : rect.right, clientY: north ? rect.top : rect.bottom };
        dispatch('pointerdown', start);
        dispatch('pointermove', { clientX: start.clientX + (west ? 500 : -500), clientY: start.clientY + (north ? 500 : -500) });
        assert.equal(stage.style.width, `${(west ? 380 : 160) + 4}px`);
        assert.equal(stage.style.height, `${(north ? 176 : 64) + 34}px`);
        assert.equal(parseFloat(widgets[2].style.left) - parseFloat(widgets[1].style.left), 160);
        assert.equal(parseFloat(widgets[2].style.top) - parseFloat(widgets[1].style.top), 88);
        assert.deepEqual(widgets[0].style, controlStyle);
        dispatch('pointercancel');
        assert.deepEqual(widgets[0].style, controlStyle);
        assert.deepEqual(widgets.slice(1).map(widget => widget.style.left), ['20px', '180px']);
        assert.deepEqual(calls, []);
    });
}

test('a 28px mandatory control keeps its small size while dragging and accepts ordinary keyboard nudges', () => {
    const { widgets, dispatch, calls } = setup({ widgets: [{ ...requiredControl, x: 80, y: 48 }] });
    const control = widgets[0];
    dispatch('pointerdown', { target: control.grip });
    dispatch('pointermove', { clientX: 6, clientY: -8 });
    assert.equal(control.style.left, '92px');
    assert.equal(control.style.top, '32px');
    assert.equal(control.style.width, '28px');
    assert.equal(control.style.height, '28px');
    dispatch('pointerup');
    assert.deepEqual(calls.at(-1), ['CommitWidgetGeometry', 'overlay-lock', 92, 32, 28, 28]);
    dispatch('keydown', { target: control, key: 'ArrowRight', shiftKey: true });
    assert.deepEqual(calls.at(-1), ['NudgeWidget', 'overlay-lock', 8, 0]);
    dispatch('keydown', { target: control, key: 'ArrowDown' });
    assert.deepEqual(calls.at(-1), ['NudgeWidget', 'overlay-lock', 0, 1]);
    assert.equal(control.style.width, '28px');
    assert.equal(control.style.height, '28px');
});

for (const corner of ['nw', 'ne', 'sw', 'se']) {
    test(`${corner} expansion at 50% with chrome preserves widget size, spacing and screen positions`, () => {
        const { stage, dispatch, calls, widgets, observers, canvasHandle, document } = setup({ corner, widgets: spacedWidgets });
        const before = stage.getBoundingClientRect();
        const widgetRects = widgets.map(widget => widget.getBoundingClientRect());
        const west = corner.includes('w'), north = corner.includes('n');
        const start = { clientX: west ? before.left : before.right, clientY: north ? before.top : before.bottom };
        dispatch('pointerdown', start);
        for (const [dx, dy] of [[50, 20], [60, 25]]) {
            dispatch('pointermove', { clientX: start.clientX + (west ? -dx : dx), clientY: start.clientY + (north ? -dy : dy) });
            // Simulate the observer callback caused by a live snapshot mid-drag.
            observers[1].callback([]);
            const current = stage.getBoundingClientRect();
            assert.equal(current[west ? 'right' : 'left'], before[west ? 'right' : 'left']);
            assert.equal(current[north ? 'bottom' : 'top'], before[north ? 'bottom' : 'top']);
            assert.equal(current.width, before.width + dx);
            assert.equal(current.height, before.height + dy);
            assert.deepEqual(widgets.map(widget => widget.getBoundingClientRect()), widgetRects);
            assert.equal(parseFloat(widgets[1].style.left) - parseFloat(widgets[0].style.left), 160);
            assert.equal(parseFloat(widgets[1].style.top) - parseFloat(widgets[0].style.top), 88);
            assert.equal(document.body.classList.contains('oe-resizing-reverse'), west !== north);
        }
        assert.equal(stage.style.width, '524px');
        assert.equal(stage.style.height, '284px');
        assert.equal(stage.dataset.width, '400');
        assert.equal(stage.dataset.height, '200');
        dispatch('pointerup');
        assert.deepEqual(calls, [['CommitCanvasCorner', 520, 250, corner]]);
        assert.equal(canvasHandle.captured.size, 0);
        assert.equal(document.body.classList.contains('oe-resizing-reverse'), false);
    });
}

for (const corner of ['nw', 'ne', 'sw']) {
    test(`${corner} shrinking stops at the first module and cancellation restores the complete layout`, () => {
        const { stage, wrap, dispatch, calls, widgets } = setup({ corner, widgets: spacedWidgets });
        const original = widgets.map(widget => ({ ...widget.style }));
        const rect = stage.getBoundingClientRect();
        const west = corner.includes('w'), north = corner.includes('n');
        const start = { clientX: west ? rect.left : rect.right, clientY: north ? rect.top : rect.bottom };
        dispatch('pointerdown', start);
        dispatch('pointermove', { clientX: start.clientX + (west ? 500 : -500), clientY: start.clientY + (north ? 500 : -500) });
        assert.equal(stage.style.width, `${(west ? 380 : 160) + 4}px`);
        assert.equal(stage.style.height, `${(north ? 176 : 64) + 34}px`);
        assert.equal(parseFloat(widgets[1].style.left) - parseFloat(widgets[0].style.left), 160);
        assert.equal(parseFloat(widgets[1].style.top) - parseFloat(widgets[0].style.top), 88);
        assert.ok(widgets.every(widget => parseFloat(widget.style.left) >= 0 && parseFloat(widget.style.top) >= 0));
        dispatch('pointercancel');
        assert.deepEqual(widgets.map(widget => widget.style), original);
        assert.equal(stage.style.width, '404px');
        assert.equal(stage.style.height, '234px');
        assert.equal(wrap.style.left, '');
        assert.equal(wrap.style.top, '');
        assert.deepEqual(calls, []);
    });
}

test('the existing true-valued southeast handle remains compatible with the corner callback', () => {
    const { canvasHandle, dispatch, calls } = setup();
    canvasHandle.dataset.canvasResize = 'true';
    dispatch('pointerdown', { clientX: 302, clientY: 167 });
    dispatch('pointermove', { clientX: 352, clientY: 187 });
    dispatch('pointerup');
    assert.deepEqual(calls, [['CommitCanvasCorner', 500, 240, 'true']]);
});

const unequalHeightWidgets = [
    { x: 20, y: 9, width: 160, height: 61 },
    { x: 20, y: 83, width: 160, height: 79 },
    { x: 25, y: 210, width: 160, height: 60 }
];

function dragThird(editor, modifiers = {}) {
    editor.dispatch('pointerdown', { target: editor.widgets[2].grip });
    // At 50% scale, the raw origin is (23, 178). The equal gap target is y=175,
    // deliberately between 8px grid points because the two prior heights differ.
    editor.dispatch('pointermove', { clientX: -1, clientY: -16, ...modifiers });
}

test('auto-align commits the exact repeated gap above grid snapping at a scaled preview with chrome', () => {
    const editor = setup({ snap: true, autoAlign: true, widgets: unequalHeightWidgets });
    const untouched = editor.widgets.slice(0, 2).map(widget => ({ ...widget.style }));
    dragThird(editor);
    assert.equal(editor.widgets[2].style.left, '20px');
    assert.equal(editor.widgets[2].style.top, '175px');
    assert.ok(editor.guideLayer.children.some(line => line.className.includes('is-gap')));
    assert.equal(editor.guideLayer.style['--guide-scale'], '2');
    editor.dispatch('pointerup');
    assert.deepEqual(editor.calls.at(-1), ['CommitWidgetGeometry', 'widget-2', 20, 175, 160, 60]);
    assert.deepEqual(editor.widgets.slice(0, 2).map(widget => widget.style), untouched);
    assert.equal(editor.guideLayer.children.length, 0);
});

for (const autoAlign of [true, false]) {
    test(`grid remains independent when auto-align is ${autoAlign ? 'temporarily bypassed with Alt' : 'disabled'}`, () => {
        const editor = setup({ snap: true, autoAlign, widgets: unequalHeightWidgets });
        dragThird(editor, { altKey: autoAlign });
        assert.equal(editor.widgets[2].style.left, '24px');
        assert.equal(editor.widgets[2].style.top, '176px');
        assert.equal(editor.guideLayer.children.length, 0);
        editor.dispatch('pointerup');
        assert.deepEqual(editor.calls.at(-1), ['CommitWidgetGeometry', 'widget-2', 24, 176, 160, 60]);
    });
}

for (const cancellation of ['pointercancel', 'Escape']) {
    test(`${cancellation} removes auto-align guides and restores the widget without saving`, () => {
        const editor = setup({ autoAlign: true, widgets: unequalHeightWidgets });
        dragThird(editor);
        assert.ok(editor.guideLayer.children.length > 0);
        if (cancellation === 'Escape') editor.dispatch('keydown', { key: 'Escape' });
        else editor.dispatch('pointercancel');
        assert.deepEqual(editor.widgets[2].style, { left: '25px', top: '210px', width: '160px', height: '60px' });
        assert.equal(editor.guideLayer.children.length, 0);
        assert.ok(editor.calls.every(call => call[0] !== 'CommitWidgetGeometry'));
        assert.equal(editor.widgets[2].grip.captured.size, 0);
    });
}

test('new modules use their declared dimensions for equal-gap alignment and content-relative drop coordinates', () => {
    const editor = setup({ snap: true, autoAlign: true, widgets: unequalHeightWidgets.slice(0, 2) });
    const rect = editor.content.getBoundingClientRect();
    const point = { clientX: rect.left + 11.5, clientY: rect.top + 89 };
    editor.dispatch('pointerdown', { target: editor.module });
    editor.dispatch('pointermove', point);
    assert.ok(editor.guideLayer.children.length > 0);
    editor.dispatch('pointerup', point);
    assert.deepEqual(editor.calls.at(-1), ['AddModuleAt', 'duration', 20, 175]);
    assert.equal(editor.guideLayer.children.length, 0);
});

test('widget resizing aligns the right edge without rounding it back onto the grid', () => {
    const editor = setup({ snap: true, autoAlign: true, widgets: [
        { x: 20, y: 10, width: 173, height: 60 },
        { x: 20, y: 100, width: 140, height: 60 }
    ] });
    editor.dispatch('pointerdown', { target: editor.widgets[1].resize });
    editor.dispatch('pointermove', { clientX: 16, clientY: 0 });
    assert.equal(editor.widgets[1].style.width, '173px');
    editor.dispatch('pointerup');
    assert.deepEqual(editor.calls.at(-1), ['CommitWidgetGeometry', 'widget-1', 20, 100, 173, 60]);
});

for (const completion of ['pointerup', 'pointercancel', 'Escape', 'unmount']) {
    test(`${completion} removes the scoped drag ghost and every document cursor state`, () => {
        const editor = setup();
        const rect = editor.content.getBoundingClientRect();
        editor.dispatch('pointerdown', { target: editor.module });
        editor.dispatch('pointermove', { clientX: rect.left + 20, clientY: rect.top + 20 });
        assert.equal(editor.ghosts[0].className, 'overlay-editor oe-drag-ghost');
        assert.equal(editor.document.body.classList.contains('oe-dragging'), true);
        if (completion === 'unmount') editor.api.unmount('editor');
        else if (completion === 'Escape') editor.dispatch('keydown', { key: 'Escape' });
        else editor.dispatch(completion, { clientX: rect.left + 20, clientY: rect.top + 20 });
        for (const state of ['oe-dragging', 'oe-resizing', 'oe-resizing-reverse'])
            assert.equal(editor.document.body.classList.contains(state), false);
        assert.equal(editor.ghosts[0].removed, true);
        assert.equal(editor.module.captured.size, 0);
        if (completion === 'unmount') assert.equal(editor.document.listeners.size, 0);
    });
}

test('unmount during a reverse canvas resize releases capture and removes document listeners', () => {
    const editor = setup({ corner: 'ne' });
    editor.dispatch('pointerdown');
    editor.dispatch('pointermove', { clientX: 70, clientY: 20 });
    assert.equal(editor.document.body.classList.contains('oe-resizing'), true);
    assert.equal(editor.document.body.classList.contains('oe-resizing-reverse'), true);
    editor.api.unmount('editor');
    editor.api.unmount('editor');
    for (const state of ['oe-dragging', 'oe-resizing', 'oe-resizing-reverse'])
        assert.equal(editor.document.body.classList.contains(state), false);
    assert.equal(editor.canvasHandle.captured.size, 0);
    assert.equal(editor.document.listeners.size, 0);
    assert.equal(editor.calls.length, 0);
});
