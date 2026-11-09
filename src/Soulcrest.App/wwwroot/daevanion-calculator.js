const bindings = new WeakMap();

export function attachPriorityTable(element, receiver) {
    detachPriorityTable(element);
    let dragged = null;
    const clear = () => {
        element.querySelectorAll('.dc-dragging,.dc-drop-target,.dc-drop-after').forEach(node => node.classList.remove('dc-dragging', 'dc-drop-target', 'dc-drop-after'));
        dragged = null;
    };
    const enabled = () => element.dataset.busy !== 'true';
    const start = event => {
        const row = event.target.closest('tr[data-priority-key]');
        if (!row || !enabled() || event.target.closest('input,select,textarea,button')) { event.preventDefault(); return; }
        dragged = row.dataset.priorityKey;
        row.classList.add('dc-dragging');
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', dragged);
    };
    const over = event => {
        if (dragged === null || !enabled()) return;
        const body = event.target.closest('tbody[data-drop-ignore]');
        if (!body) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        element.querySelectorAll('.dc-drop-target,.dc-drop-after').forEach(node => node.classList.remove('dc-drop-target', 'dc-drop-after'));
        const row = event.target.closest('tr[data-priority-key]');
        const after = row && event.clientY > row.getBoundingClientRect().top + row.getBoundingClientRect().height / 2;
        (row || body).classList.add(after ? 'dc-drop-after' : 'dc-drop-target');
    };
    const drop = event => {
        if (dragged === null || !enabled()) return;
        const body = event.target.closest('tbody[data-drop-ignore]');
        if (!body) return;
        event.preventDefault();
        const row = event.target.closest('tr[data-priority-key]');
        const key = dragged;
        if (row?.dataset.priorityKey === key) { clear(); return; }
        const after = row && event.clientY > row.getBoundingClientRect().top + row.getBoundingClientRect().height / 2;
        const beforeKey = !after ? row?.dataset.priorityKey || null : null;
        const afterKey = after ? row.dataset.priorityKey : null;
        clear();
        receiver.invokeMethodAsync('MoveFromDrop', key, beforeKey, afterKey, body.dataset.dropIgnore === 'true', body.dataset.dropRequired === 'true').catch(() => {});
    };
    const handlers = { dragstart: start, dragover: over, drop, dragend: clear };
    for (const [name, handler] of Object.entries(handlers)) element.addEventListener(name, handler);
    bindings.set(element, handlers);
}

export function detachPriorityTable(element) {
    const handlers = bindings.get(element);
    if (!handlers) return;
    for (const [name, handler] of Object.entries(handlers)) element.removeEventListener(name, handler);
    bindings.delete(element);
}
