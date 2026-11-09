// Ported from overlay-editor-kit 1.15.0; original test scenarios preserved.
// Run with: node --test tests/overlay-kit-dialogs.test.cjs
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const script = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/overlay-kit/dialogs.js'), 'utf8');

function setup(backdropClose = true) {
    const listeners = [];
    let closeClicks = 0;
    const closeButton = { focus() {}, click() { closeClicks++; dialog.open = false; } };
    const dialog = {
        open: false, dataset: {},
        hasAttribute(name) { return name === 'data-backdrop-close' && backdropClose; },
        addEventListener(name, handler) { if (name === 'click') listeners.push(handler); },
        showModal() { this.open = true; },
        getBoundingClientRect() { return { left: 100, top: 100, right: 500, bottom: 400 }; },
        querySelector(selector) { return selector === '[data-dialog-close]' || selector === 'button, input, select' ? closeButton : null; },
    };
    const window = {};
    const document = { getElementById(id) { return id === 'share-dialog' ? dialog : null; } };
    vm.runInNewContext(script, { window, document });
    return { api: window.soulcrestOverlayDialogs, dialog, listeners, closeClicks: () => closeClicks };
}

test('share dialog closes only when the backdrop is clicked', () => {
    const { api, dialog, listeners, closeClicks } = setup();
    api.showDialog('share-dialog');
    assert.equal(listeners.length, 1);

    listeners[0]({ target: dialog, clientX: 300, clientY: 200 });
    listeners[0]({ target: {}, clientX: 50, clientY: 50 });
    assert.equal(closeClicks(), 0);
    assert.equal(dialog.open, true);

    listeners[0]({ target: dialog, clientX: 50, clientY: 200 });
    assert.equal(closeClicks(), 1);
    assert.equal(dialog.open, false);

    api.showDialog('share-dialog');
    assert.equal(listeners.length, 1);
    listeners[0]({ target: dialog, clientX: 300, clientY: 450 });
    assert.equal(closeClicks(), 2);
});

test('other dialogs keep their existing backdrop behavior', () => {
    const { api, listeners, closeClicks } = setup(false);
    api.showDialog('share-dialog');
    assert.equal(listeners.length, 0);
    assert.equal(closeClicks(), 0);
});
