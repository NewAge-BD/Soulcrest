const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/daevanion-calculator.js'), 'utf8');
const moduleReady = import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

function fixture() {
    const listeners = new Map();
    const calls = [];
    const nodes = [];
    const root = {
        dataset: { busy: 'false' },
        addEventListener: (name, callback) => listeners.set(name, callback),
        removeEventListener: name => listeners.delete(name),
        querySelectorAll: selector => nodes.filter(node => selector.split(',').some(value => node.classes.has(value.slice(1))))
    };
    const body = { dataset: { dropIgnore: 'false', dropRequired: 'false' } };
    const ignoredBody = { dataset: { dropIgnore: 'true', dropRequired: 'false' } };
    const requiredBody = { dataset: { dropIgnore: 'false', dropRequired: 'true' } };
    const node = (key, parent) => {
        const item = {
            dataset: key ? { priorityKey: key } : {}, classes: new Set(), nextElementSibling: null,
            closest: selector => selector === 'input,select,textarea,button' ? null : selector.startsWith('tr') ? (key ? item : null) : parent,
            getBoundingClientRect: () => ({ top: 100, height: 20 })
        };
        item.classList = { add: (...values) => values.forEach(value => item.classes.add(value)), remove: (...values) => values.forEach(value => item.classes.delete(value)) };
        nodes.push(item); return item;
    };
    const first = node('attack', body); const second = node('crit', body); const third = node('hp', body);
    first.nextElementSibling = second; second.nextElementSibling = third;
    const ignoreTarget = node(null, ignoredBody);
    const requiredTarget = node(null, requiredBody);
    const requiredRow = node('required-attack', requiredBody);
    const event = (target, y = 101) => ({ target, clientY: y, dataTransfer: { setData() {} }, preventDefault() { this.prevented = true; } });
    return { root, listeners, calls, nodes, first, second, third, ignoreTarget, requiredTarget, requiredRow, event,
        receiver: { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } } };
}

test('priority drag emits exact global before or after anchors even at the filtered end', async () => {
    const api = await moduleReady; const f = fixture(); api.attachPriorityTable(f.root, f.receiver);
    f.listeners.get('dragstart')(f.event(f.third));
    f.listeners.get('drop')(f.event(f.first, 101));
    assert.deepEqual(f.calls.pop(), ['MoveFromDrop', 'hp', 'attack', null, false, false]);
    f.listeners.get('dragstart')(f.event(f.first));
    f.listeners.get('drop')(f.event(f.third, 119));
    assert.deepEqual(f.calls.pop(), ['MoveFromDrop', 'attack', null, 'hp', false, false]);
    assert.ok(f.nodes.every(node => node.classes.size === 0));
});

test('ignore section emits soft avoidance without moving between categories', async () => {
    const api = await moduleReady; const f = fixture(); api.attachPriorityTable(f.root, f.receiver);
    f.listeners.get('drop')(f.event(f.ignoreTarget));
    assert.deepEqual(f.calls, []); // A drag originating in another table has no local source.
    f.listeners.get('dragstart')(f.event(f.first));
    f.listeners.get('drop')(f.event(f.ignoreTarget));
    assert.deepEqual(f.calls, [['MoveFromDrop', 'attack', null, null, true, false]]);
});

test('required normal and ignore drops set mutually exclusive hard and soft constraints', async () => {
    const api = await moduleReady; const f = fixture(); api.attachPriorityTable(f.root, f.receiver);
    f.listeners.get('dragstart')(f.event(f.first));
    f.listeners.get('drop')(f.event(f.requiredTarget));
    assert.deepEqual(f.calls.pop(), ['MoveFromDrop', 'attack', null, null, false, true]);
    f.listeners.get('dragstart')(f.event(f.first));
    f.listeners.get('drop')(f.event(f.requiredRow, 119));
    assert.deepEqual(f.calls.pop(), ['MoveFromDrop', 'attack', null, 'required-attack', false, true]);
    f.listeners.get('dragstart')(f.event(f.requiredRow));
    f.listeners.get('drop')(f.event(f.second, 101));
    assert.deepEqual(f.calls.pop(), ['MoveFromDrop', 'required-attack', 'crit', null, false, false]);
    f.listeners.get('dragstart')(f.event(f.requiredRow));
    f.listeners.get('drop')(f.event(f.ignoreTarget));
    assert.deepEqual(f.calls.pop(), ['MoveFromDrop', 'required-attack', null, null, true, false]);
});

test('dropping onto either half of the dragged row emits no mutation', async () => {
    const api = await moduleReady; const f = fixture(); api.attachPriorityTable(f.root, f.receiver);
    for (const y of [101, 119]) {
        f.listeners.get('dragstart')(f.event(f.first));
        f.listeners.get('drop')(f.event(f.first, y));
    }
    assert.deepEqual(f.calls, []);
    assert.ok(f.nodes.every(node => node.classes.size === 0));
});

test('busy tables reject drags and detached tables retain no event handlers', async () => {
    const api = await moduleReady; const f = fixture(); api.attachPriorityTable(f.root, f.receiver);
    f.root.dataset.busy = 'true';
    const start = f.event(f.first); f.listeners.get('dragstart')(start);
    assert.equal(start.prevented, true);
    f.listeners.get('drop')(f.event(f.second)); assert.deepEqual(f.calls, []);
    api.attachPriorityTable(f.root, f.receiver); assert.equal(f.listeners.size, 4);
    api.detachPriorityTable(f.root); assert.equal(f.listeners.size, 0);
});

test('editing a skill level never starts a priority drag', async () => {
    const api = await moduleReady; const f = fixture(); api.attachPriorityTable(f.root, f.receiver);
    const input = { closest: selector => selector === 'input,select,textarea,button' ? input : f.first };
    const start = f.event(input); f.listeners.get('dragstart')(start);
    assert.equal(start.prevented, true);
    f.listeners.get('drop')(f.event(f.second));
    assert.deepEqual(f.calls, []);
});
