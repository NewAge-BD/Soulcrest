const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function fixture() {
    const document = { activeElement: null };
    const field = id => ({ id, focus() { document.activeElement = this; } });
    const close = field('close');
    const inputs = [field('resource-map-world'), field('resource-map-mini'), field('resource-map-soulcrest')];
    const confirm = field('confirm');
    const fields = [close, ...inputs, confirm];
    const listeners = [];
    const dialog = {
        dataset: {}, contains: element => fields.includes(element),
        querySelector: selector => selector === 'input' ? inputs[0] : null,
        querySelectorAll: selector => selector === '.resource-map-options input' ? inputs : fields,
        addEventListener: (name, listener) => { assert.equal(name, 'keydown'); listeners.push(listener); }
    };
    document.querySelector = () => dialog;
    const context = { window: {}, document };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/resource-map.js'), 'utf8'), context);
    return { api: context.window.soulcrestResourceMap, document, dialog, close, inputs, confirm, listeners };
}

test('opening the resource dialog focuses a real input and represents mixed map settings', () => {
    const f = fixture();
    f.api.dialog(['resource-map-mini']);
    assert.equal(f.document.activeElement, f.inputs[0]);
    assert.deepEqual(f.inputs.map(input => input.indeterminate), [false, true, false]);
    f.document.activeElement = f.inputs[2];
    f.api.dialog([]);
    assert.equal(f.document.activeElement, f.inputs[2]);
    assert.deepEqual(f.inputs.map(input => input.indeterminate), [false, false, false]);
    assert.equal(f.listeners.length, 1);
});

test('keyboard focus wraps in both directions and ordinary navigation remains untouched', () => {
    const f = fixture();
    f.api.dialog([]);
    const key = (key, shiftKey = false) => {
        let prevented = false;
        f.listeners[0]({ key, shiftKey, preventDefault() { prevented = true; } });
        return prevented;
    };
    f.document.activeElement = f.confirm;
    assert.equal(key('Tab'), true);
    assert.equal(f.document.activeElement, f.close);
    assert.equal(key('Tab', true), true);
    assert.equal(f.document.activeElement, f.confirm);
    f.document.activeElement = f.inputs[1];
    assert.equal(key('Tab'), false);
    assert.equal(key('Escape'), false);
    assert.equal(f.document.activeElement, f.inputs[1]);
});

test('a dialog already removed during a UI update is harmless', () => {
    const f = fixture();
    f.document.querySelector = () => null;
    assert.doesNotThrow(() => f.api.dialog(['resource-map-world']));
    assert.equal(f.listeners.length, 0);
});
