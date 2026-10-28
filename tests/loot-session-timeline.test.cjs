const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function fixture() {
    const observers = [];
    class ResizeObserver {
        constructor(callback) { this.callback = callback; observers.push(this); }
        observe(element) { this.element = element; }
        disconnect() { this.disconnected = true; }
    }
    const context = { window: {}, ResizeObserver };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/loot-session-timeline.js'), 'utf8'), context);
    const listeners = new Map(), captured = new Set();
    const element = {
        addEventListener(name, callback, options) { const entries = listeners.get(name) || []; entries.push({ callback, options }); listeners.set(name, entries); },
        removeEventListener(name, callback) { listeners.set(name, (listeners.get(name) || []).filter(entry => entry.callback !== callback)); },
        getBoundingClientRect: () => ({ width: 640, left: 112 }),
        contains: child => child.parent === element,
        focus(options) { this.focusOptions = options; },
        setPointerCapture(id) { captured.add(id); },
        hasPointerCapture: id => captured.has(id),
        releasePointerCapture(id) { captured.delete(id); }
    };
    function dispatch(name, values = {}) {
        const event = { target: element, preventDefault() { this.prevented = true; }, ...values };
        for (const entry of listeners.get(name) || []) entry.callback(event);
        return event;
    }
    return { api: context.window.soulcrestLootTimeline, element, observers, listeners, captured, dispatch };
}

test('wheel handling is scoped to Shift-wheel and preserves ordinary and browser zoom scrolling', () => {
    const f = fixture(); f.api.attach(f.element);
    assert.equal(f.listeners.get('wheel')[0].options.passive, false);
    assert.equal(f.dispatch('wheel').prevented, undefined);
    assert.equal(f.dispatch('wheel', { shiftKey: true }).prevented, true);
    assert.equal(f.dispatch('wheel', { shiftKey: true, ctrlKey: true }).prevented, undefined);
});

test('track and dedicated playhead keys prevent page movement while native inputs and other buttons keep theirs', () => {
    const f = fixture(); f.api.attach(f.element);
    for (const key of ['+', '-', 'ArrowLeft', 'ArrowRight', 'Home', ' ']) assert.equal(f.dispatch('keydown', { key }).prevented, true, key);
    for (const key of ['ArrowUp', 'ArrowDown', 'End', 'Tab', 'Escape', 'PageDown']) assert.equal(f.dispatch('keydown', { key }).prevented, undefined, key);
    const input = { closest: () => null };
    assert.equal(f.dispatch('keydown', { key: ' ', target: input }).prevented, undefined);
    assert.equal(f.dispatch('keydown', { key: 'ArrowLeft', target: input }).prevented, undefined);
    const grip = { parent: f.element, closest() { return this; } };
    assert.equal(f.dispatch('keydown', { key: 'End', target: grip }).prevented, true);
    assert.equal(f.dispatch('keydown', { key: '+', target: grip }).prevented, undefined);
    assert.equal(f.dispatch('keydown', { key: ' ', ctrlKey: true }).prevented, undefined);
});

test('attach is idempotent, reports real measured pixels and detaches every observer and listener', async () => {
    const f = fixture(), calls = [];
    const callback = { invokeMethodAsync(...args) { calls.push(args); return Promise.resolve(); } };
    f.api.attach(f.element, callback); f.api.attach(f.element, callback);
    assert.equal(f.observers[0].disconnected, true);
    assert.equal(f.listeners.get('keydown').length, 1);
    f.observers[1].callback();
    assert.deepEqual(calls, [['TimelineResized', 640, 112]]);
    assert.deepEqual(Array.from(f.api.bounds(f.element)), [640, 112]);
    f.api.detach(f.element); f.observers[1].callback();
    assert.equal(f.observers[1].disconnected, true);
    assert.equal(f.listeners.get('keydown').length, 0);
    assert.equal(f.listeners.get('wheel').length, 0);
    assert.equal(calls.length, 1);
    f.api.detach(f.element);
});

test('pointer capture focuses only its target and release tolerates an already released pointer', () => {
    const f = fixture();
    f.api.capture(f.element, 7);
    assert.equal(f.element.focusOptions.preventScroll, true);
    assert.equal(f.captured.has(7), true);
    f.api.release(f.element, 7); f.api.release(f.element, 7);
    assert.equal(f.captured.size, 0);
    f.element.setPointerCapture = () => { throw new Error('pointer ended'); };
    assert.doesNotThrow(() => f.api.capture(f.element, 9));
});

test('released and superseded pointer generations cannot capture late or release a newer gesture', () => {
    const f = fixture();
    f.api.release(f.element, 7, 1);
    f.api.capture(f.element, 7, 1);
    assert.equal(f.captured.size, 0);
    f.api.capture(f.element, 7, 2);
    assert.equal(f.captured.has(7), true);
    f.api.release(f.element, 7, 1);
    assert.equal(f.captured.has(7), true);
    f.api.capture(f.element, 9, 1);
    assert.equal(f.captured.has(9), false);
    f.api.release(f.element, 7, 2);
    f.api.capture(f.element, 7, 2);
    assert.equal(f.captured.size, 0);
});

test('native pointerdown captures background or head synchronously, skips controls and detaches cleanly', () => {
    const f = fixture(); f.api.attach(f.element);
    const control = { closest: selector => selector === '[data-playhead-grip]' ? null : control };
    f.dispatch('pointerdown', { button: 0, pointerId: 7, target: control });
    assert.equal(f.captured.size, 0);
    f.dispatch('pointerdown', { button: 0, pointerId: 7 });
    assert.equal(f.captured.has(7), true);
    f.dispatch('pointerdown', { button: 0, pointerId: 8 });
    assert.equal(f.captured.has(8), false);
    f.dispatch('pointerup', { pointerId: 7 });
    f.api.release(f.element, 7);
    const head = { ...f.element, parent: f.element, closest: () => head };
    f.dispatch('pointerdown', { button: 0, pointerId: 8, target: head });
    assert.equal(f.captured.has(8), true);
    assert.equal(head.focusOptions.preventScroll, true);
    f.api.detach(f.element);
    for (const key of ['pointerdown', 'pointerup', 'pointercancel']) assert.equal(f.listeners.get(key).length, 0);
});
