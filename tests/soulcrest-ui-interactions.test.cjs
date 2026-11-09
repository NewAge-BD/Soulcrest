const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function fixture() {
    const listeners = new Map();
    class Element {
        constructor(matches = {}) { this.matches = matches; }
        closest(selector) {
            if (selector === '.topbar .tabs') return this.matches.tabs ?? null;
            if (selector === '.boss-timers-compact .boss-timer-card') return this.matches.card ?? null;
            return this.matches.control ?? null;
        }
    }
    const window = { addEventListener() {}, getSelection: () => ({ toString: () => selection }) };
    let selection = '';
    const context = { window, Element, document: {
        addEventListener(name, callback, options) { listeners.set(name, { callback, options }); }
    } };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-ui.js'), 'utf8'), context);
    function dispatch(name, target, values = {}) {
        const event = { target, button: 0, deltaX: 0, deltaY: 80, deltaMode: 0,
            preventDefault() { this.defaultPrevented = true; }, ...values };
        listeners.get(name).callback(event);
        return event;
    }
    let clicks = 0;
    const summary = new Element({ control: true });
    const card = { querySelector: () => summary };
    summary.matches.card = card;
    summary.click = () => { clicks++; dispatch('click', summary); };
    let scrollLeft = 0;
    const tabs = { clientWidth: 300, scrollWidth: 1200,
        get scrollLeft() { return scrollLeft; },
        set scrollLeft(value) { scrollLeft = Math.max(0, Math.min(value, this.scrollWidth - this.clientWidth)); }
    };
    return { Element, card, tabs, summary, listeners, dispatch,
        get clicks() { return clicks; }, set selection(value) { selection = value; } };
}

test('clicking free boss card areas forwards exactly once to the existing disclosure', () => {
    const f = fixture();
    for (const area of ['title', 'portrait', 'countdown', 'map', 'background', 'expanded background']) {
        const before = f.clicks;
        f.dispatch('click', new f.Element({ card: f.card }));
        assert.equal(f.clicks, before + 1, area);
    }
    // Native summary clicks must not be forwarded recursively or toggled twice.
    const before = f.clicks;
    f.summary.click();
    assert.equal(f.clicks, before + 1);
});

test('boss card inputs, their labels and action buttons keep their own click behavior', () => {
    const f = fixture();
    for (const control of ['input', 'label', 'button', 'select', 'textarea', 'link', 'summary', 'role button', 'editable']) {
        const target = new f.Element({ card: f.card, control });
        const event = f.dispatch('click', target);
        assert.equal(f.clicks, 0, control);
        assert.equal(event.defaultPrevented, undefined, control);
    }
});

test('secondary clicks, handled clicks, text selection and other cards do not toggle settings', () => {
    const f = fixture();
    const target = new f.Element({ card: f.card });
    f.dispatch('click', target, { button: 1 });
    f.dispatch('click', target, { button: 2 });
    f.dispatch('click', target, { defaultPrevented: true });
    f.selection = 'boss name';
    f.dispatch('click', target);
    f.selection = '';
    f.dispatch('click', new f.Element());
    assert.equal(f.clicks, 0);
});

test('a text-node click inside a boss card also reaches the disclosure', () => {
    const f = fixture();
    f.dispatch('click', { parentElement: new f.Element({ card: f.card }) });
    assert.equal(f.clicks, 1);
});

test('ordinary wheel movement scrolls main tabs in both directions and leaves boundaries alone', () => {
    const f = fixture();
    const target = new f.Element({ tabs: f.tabs });
    assert.equal(f.listeners.get('wheel').options.passive, false);
    assert.equal(f.dispatch('wheel', target).defaultPrevented, true);
    assert.equal(f.tabs.scrollLeft, 80);
    f.dispatch('wheel', target, { deltaY: 2000 });
    assert.equal(f.tabs.scrollLeft, 900);
    assert.equal(f.dispatch('wheel', target).defaultPrevented, undefined);
    f.dispatch('wheel', target, { deltaY: -2000 });
    assert.equal(f.tabs.scrollLeft, 0);
    assert.equal(f.dispatch('wheel', target, { deltaY: -80 }).defaultPrevented, undefined);
});

test('native horizontal wheel, browser zoom and scrolling outside overflowing main tabs are preserved', () => {
    const f = fixture();
    const target = new f.Element({ tabs: f.tabs });
    for (const values of [{ deltaX: 120 }, { ctrlKey: true }, { defaultPrevented: true }]) {
        f.dispatch('wheel', target, values);
        assert.equal(f.tabs.scrollLeft, 0);
    }
    assert.equal(f.dispatch('wheel', new f.Element()).defaultPrevented, undefined);
    f.tabs.scrollWidth = f.tabs.clientWidth;
    assert.equal(f.dispatch('wheel', target).defaultPrevented, undefined);
    assert.equal(f.tabs.scrollLeft, 0);
});

test('line and page wheel units have usable distances in narrow windows', () => {
    const f = fixture();
    const target = new f.Element({ tabs: f.tabs });
    f.dispatch('wheel', target, { deltaMode: 1, deltaY: 3 });
    assert.equal(f.tabs.scrollLeft, 48);
    f.dispatch('wheel', target, { deltaMode: 2, deltaY: 1 });
    assert.equal(f.tabs.scrollLeft, 348);
});
