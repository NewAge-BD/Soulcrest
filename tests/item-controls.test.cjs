const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

class Target {
  constructor() { this.events = new Map(); }
  addEventListener(name, listener) { if (!this.events.has(name)) this.events.set(name, new Set()); this.events.get(name).add(listener); }
  removeEventListener(name, listener) { this.events.get(name)?.delete(listener); }
  fire(name, event = {}) { for (const listener of this.events.get(name) ?? []) listener(event); }
  count(name) { return this.events.get(name)?.size ?? 0; }
}

function harness() {
  const window = new Target(); const document = new Target(); document.activeElement = null;
  const context = vm.createContext({ window, document, innerWidth: 800, innerHeight: 600, CSS: { escape: String } });
  const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/item-controls.js'), 'utf8').replace(/^export /gm, '');
  vm.runInContext(source + '\nthis.api={bindSelectKeyboard,placePopup,closePopupFor,bindTooltipParent,disposeTooltipParent};', context);
  return { ...context.api, window, document };
}

function element(bounds) {
  const node = new Target(); node.style = {}; node.attributes = new Map(); node.isConnected = true;
  node.setAttribute = (name, value) => node.attributes.set(name, String(value));
  node.getAttribute = name => node.attributes.get(name) ?? null;
  node.removeAttribute = name => node.attributes.delete(name);
  node.getBoundingClientRect = () => ({ ...bounds,
    width: Math.min(bounds.width, Number.parseFloat(node.style.width) || Infinity, Number.parseFloat(node.style.maxWidth) || Infinity),
    height: Math.min(bounds.height, Number.parseFloat(node.style.maxHeight) || Infinity) });
  node.closest = () => null; node.contains = () => false; node.querySelector = () => null;
  return node;
}

test('manual popovers escape their ancestors without moving the DOM and stay within the viewport', () => {
  const ui = harness();
  const trigger = element({ left: 760, top: 550, bottom: 580, width: 70, height: 30 });
  const popup = element({ left: 0, top: 0, width: 360, height: 220 });
  const originalParent = {}; popup.parentElement = originalParent;
  let shown = 0; let hidden = 0;
  popup.showPopover = () => shown++; popup.hidePopover = () => hidden++;
  ui.placePopup(trigger, popup, 'select', false, null);
  assert.equal(popup.getAttribute('popover'), 'manual'); assert.equal(shown, 1);
  assert.equal(popup.parentElement, originalParent);
  assert.equal(popup.style.position, 'fixed'); assert.equal(popup.style.margin, '0'); assert.equal(popup.style.inset, 'auto');
  assert.equal(popup.style.minWidth, '0', 'absolute-layout minimum must not become a viewport-wide top-layer minimum');
  const size = popup.getBoundingClientRect();
  assert.ok(Number.parseFloat(popup.style.left) >= 8);
  assert.ok(Number.parseFloat(popup.style.left) + size.width <= 792);
  assert.ok(Number.parseFloat(popup.style.top) >= 8);
  assert.ok(Number.parseFloat(popup.style.top) + size.height <= 592);
  assert.ok(Number.parseFloat(popup.style.top) < 550, 'popup opens above a trigger near the lower edge');
  assert.equal(ui.window.count('resize'), 1); assert.equal(ui.document.count('scroll'), 1);
  ui.closePopupFor(trigger);
  assert.equal(hidden, 1); assert.equal(popup.getAttribute('popover'), null);
  assert.equal(ui.window.count('resize'), 0); assert.equal(ui.document.count('scroll'), 0);
  ui.closePopupFor(trigger); assert.equal(hidden, 1);
});

test('missing or rejected popover APIs keep the fixed-position fallback usable', () => {
  for (const throws of [false, true]) {
    const ui = harness();
    const trigger = element({ left: 10, top: 10, bottom: 40, width: 220, height: 30 });
    const popup = element({ left: 0, top: 0, width: 260, height: 180 });
    if (throws) { popup.showPopover = () => { throw new Error('unavailable'); }; popup.hidePopover = () => {}; }
    ui.placePopup(trigger, popup, 'select', false, null);
    assert.equal(popup.getAttribute('popover'), null);
    assert.equal(popup.style.position, 'fixed'); assert.equal(popup.style.top, '45px');
    ui.closePopupFor(trigger);
    assert.equal(ui.window.count('resize'), 0); assert.equal(ui.document.count('scroll'), 0);
  }
});

test('repositioning replaces listeners and updates an active option without duplicate watchers', () => {
  const ui = harness();
  const trigger = element({ left: 20, top: 100, bottom: 130, width: 250, height: 30 });
  const popup = element({ left: 0, top: 0, width: 250, height: 200 });
  let scrolled = 0; popup.querySelector = () => ({ scrollIntoView: options => { assert.equal(options.block, 'nearest'); scrolled++; } });
  ui.placePopup(trigger, popup, 'select', false, 'option-2');
  ui.placePopup(trigger, popup, 'select', false, 'option-3');
  assert.equal(scrolled, 2); assert.equal(ui.window.count('resize'), 1); assert.equal(ui.document.count('scroll'), 1);
  trigger.isConnected = false; ui.window.fire('resize');
  assert.equal(ui.window.count('resize'), 0); assert.equal(ui.document.count('scroll'), 0);
});

test('hover tooltip Escape dismissal is registered only while its popup is active', () => {
  const ui = harness(); const calls = [];
  const trigger = element({ left: 100, top: 300, bottom: 330, width: 30, height: 30 });
  const popup = element({ left: 0, top: 0, width: 200, height: 70 });
  const receiver = { invokeMethodAsync: name => { calls.push(name); return Promise.resolve(); } };
  ui.placePopup(trigger, popup, 'tooltip', false, null, receiver);
  ui.document.fire('keydown', { key: 'ArrowDown' }); assert.deepEqual(calls, []);
  ui.document.fire('keydown', { key: 'Escape' }); assert.deepEqual(calls, ['Dismiss']);
  ui.closePopupFor(trigger); assert.equal(ui.document.count('keydown'), 0);
});

test('parent focus supports tabindex containers and restores existing accessible descriptions', () => {
  const ui = harness(); const calls = [];
  const parent = element({ left: 0, top: 0, bottom: 20, width: 100, height: 20 });
  parent.setAttribute('aria-describedby', 'existing-description');
  const trigger = element({ left: 0, top: 0, bottom: 20, width: 30, height: 20 });
  trigger.setAttribute('aria-describedby', 'item-description');
  trigger.closest = selector => { assert.ok(selector.includes('[tabindex]')); return parent; };
  ui.bindTooltipParent(trigger, { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } });
  assert.equal(parent.getAttribute('aria-describedby'), 'existing-description item-description');
  parent.fire('focusin'); parent.fire('keydown', { key: 'Escape' });
  assert.deepEqual(calls, [['ParentTrigger', true], ['Dismiss']]);
  ui.disposeTooltipParent(trigger);
  assert.equal(parent.getAttribute('aria-describedby'), 'existing-description');
  assert.equal(parent.count('focusin'), 0); assert.equal(parent.count('keydown'), 0);
});

test('select keyboard suppresses browser button activation but preserves Tab and ordinary typing', () => {
  const ui = harness(); const trigger = new Target(); let prevented = 0;
  ui.bindSelectKeyboard(trigger); ui.bindSelectKeyboard(trigger);
  assert.equal(trigger.count('keydown'), 1);
  for (const key of ['Enter', ' ', 'ArrowDown', 'ArrowUp', 'Home', 'End', 'Escape', 'Tab', 'a'])
    trigger.fire('keydown', { key, preventDefault: () => prevented++ });
  assert.equal(prevented, 7);
});
