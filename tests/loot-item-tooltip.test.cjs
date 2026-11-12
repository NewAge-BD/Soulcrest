const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function fixture(color = '#FF6C00') {
    const events = new Map(), windowEvents = new Map(), nodes = [];
    const anchor = { dataset: { itemNameText: '<Actual recorded item>', itemNameColor: color, itemQuantityText: '12' },
        isConnected: true, attributes: {}, closest: () => anchor, contains: other => other === anchor,
        setAttribute(key, value) { this.attributes[key] = value; }, removeAttribute(key) { delete this.attributes[key]; },
        getBoundingClientRect: () => ({ left: 1270, top: 700, bottom: 720 }) };
    const make = tag => ({ tag, style: {}, children: [], attributes: {},
        setAttribute(key, value) { this.attributes[key] = value; }, append(...children) { this.children.push(...children); },
        remove() { nodes.splice(nodes.indexOf(this), 1); }, getBoundingClientRect: () => ({ width: 250, height: 42 }) });
    let mutation;
    const document = { body: { append(node) { nodes.push(node); } }, createElement: make,
        createTextNode: text => ({ textContent: text }), addEventListener(name, fn) { events.set(name, fn); } };
    const context = { document, window: { innerWidth: 1280, innerHeight: 720,
        addEventListener(name, fn) { windowEvents.set(name, fn); } },
        MutationObserver: class { constructor(callback) { mutation = callback; } observe() {} } };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/loot-item-tooltip.js'), 'utf8'), context);
    return { anchor, nodes, mutation: () => mutation(), fire: (name, extra = {}) => events.get(name)({ target: anchor, ...extra }), windowEvents };
}

test('SVG item tooltip preserves escaped text and separate quantity, exact foreground and viewport bounds', () => {
    const f = fixture(); f.fire('pointerover');
    const bubble = f.nodes[0];
    assert.equal(bubble.children[0].textContent, '<Actual recorded item>');
    assert.equal(bubble.children[0].style.color, '#FF6C00');
    assert.equal(bubble.children[1].textContent, ' × 12');
    assert.equal(bubble.style.left, '1022px'); assert.equal(bubble.style.top, '650px');
    assert.equal(bubble.attributes.role, 'tooltip');
    assert.equal(f.anchor.attributes['aria-describedby'], bubble.id);
    f.fire('pointerout', { relatedTarget: null });
    assert.equal(f.nodes.length, 0); assert.equal(f.anchor.attributes['aria-describedby'], undefined);
});

test('keyboard focus, escape, unknown CSS and removed route markers do not leave stale tooltips', () => {
    const f = fixture('red;position:fixed'); f.fire('focusin');
    assert.equal(f.nodes[0].children[0].style.color, undefined);
    f.fire('keydown', { key: 'Escape' }); assert.equal(f.nodes.length, 0);
    f.fire('focusin'); f.anchor.isConnected = false; f.mutation(); assert.equal(f.nodes.length, 0);
    f.anchor.isConnected = true; f.fire('focusin'); f.windowEvents.get('resize')(); assert.equal(f.nodes.length, 0);
});
