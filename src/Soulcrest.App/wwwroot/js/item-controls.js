const popups = new WeakMap();
const keyboards = new WeakSet();
const parents = new WeakMap();

export function bindSelectKeyboard(trigger) {
    if (!trigger || keyboards.has(trigger)) return;
    keyboards.add(trigger);
    trigger.addEventListener('keydown', event => {
        if (['Enter', ' ', 'ArrowDown', 'ArrowUp', 'Home', 'End', 'Escape'].includes(event.key)) event.preventDefault();
    });
}

export function closePopupFor(trigger) {
    const entry = popups.get(trigger);
    if (!entry) return;
    window.removeEventListener('resize', entry.position);
    document.removeEventListener('scroll', entry.position, true);
    if (entry.key) document.removeEventListener('keydown', entry.key, true);
    if (entry.topLayer) {
        try { entry.popup.hidePopover(); } catch { /* The element may already be detached. */ }
        entry.popup.removeAttribute('popover');
    }
    popups.delete(trigger);
}

export function placePopup(trigger, popup, kind, useParent, activeId, dismissReceiver) {
    if (!trigger || !popup) return;
    closePopupFor(trigger);
    const anchor = useParent ? trigger.closest('button,[role="button"],a[href],[tabindex]:not([tabindex="-1"])') ?? trigger : trigger;
    let topLayer = false;
    popup.style.position = 'fixed';
    popup.style.margin = '0';
    popup.style.inset = 'auto';
    popup.style.minWidth = '0';
    if (typeof popup.showPopover === 'function' && typeof popup.hidePopover === 'function') {
        try {
            popup.setAttribute('popover', 'manual');
            popup.showPopover();
            topLayer = true;
        } catch {
            popup.removeAttribute('popover');
        }
    }
    const position = () => {
        if (!anchor.isConnected || !popup.isConnected) { closePopupFor(trigger); return; }
        const bounds = anchor.getBoundingClientRect();
        const padding = 8;
        const maxWidth = Math.max(120, innerWidth - padding * 2);
        popup.style.position = 'fixed';
        popup.style.maxWidth = maxWidth + 'px';
        if (kind === 'select') popup.style.width = Math.min(Math.max(bounds.width, 240), maxWidth) + 'px';
        const below = innerHeight - bounds.bottom - padding;
        const above = bounds.top - padding;
        const prefersBelow = kind === 'select';
        const room = prefersBelow ? below >= 160 || below >= above ? below : above : above >= 100 || above >= below ? above : below;
        popup.style.maxHeight = Math.max(60, Math.min(kind === 'select' ? 320 : 300, room - 6)) + 'px';
        const size = popup.getBoundingClientRect();
        const useBelow = prefersBelow ? below >= 160 || below >= above : !(above >= 100 || above >= below);
        const top = useBelow ? bounds.bottom + 5 : bounds.top - size.height - 5;
        popup.style.left = Math.max(padding, Math.min(bounds.left, innerWidth - size.width - padding)) + 'px';
        popup.style.top = Math.max(padding, Math.min(top, innerHeight - size.height - padding)) + 'px';
        popup.style.bottom = 'auto';
    };
    const key = kind === 'tooltip' && dismissReceiver ? event => {
        if (event.key === 'Escape') dismissReceiver.invokeMethodAsync('Dismiss').catch(() => {});
    } : null;
    popups.set(trigger, { position, popup, topLayer, key });
    position();
    window.addEventListener('resize', position);
    document.addEventListener('scroll', position, true);
    if (key) document.addEventListener('keydown', key, true);
    if (activeId) popup.querySelector('[id="' + CSS.escape(activeId) + '"]')?.scrollIntoView({ block: 'nearest' });
}

export function bindTooltipParent(trigger, receiver) {
    if (!trigger || parents.has(trigger)) return;
    const parent = trigger.closest('button,[role="button"],a[href],[tabindex]:not([tabindex="-1"])');
    if (!parent) return;
    const describedBy = trigger.getAttribute('aria-describedby');
    const descriptions = (parent.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);
    const addedDescription = describedBy && !descriptions.includes(describedBy);
    if (addedDescription) parent.setAttribute('aria-describedby', [...descriptions, describedBy].join(' '));
    let hovered = false;
    let focused = parent.contains(document.activeElement);
    const notify = () => receiver.invokeMethodAsync('ParentTrigger', hovered || focused).catch(() => {});
    const enter = () => { hovered = true; notify(); };
    const leave = () => { hovered = false; notify(); };
    const focus = () => { focused = true; notify(); };
    const blur = () => { focused = false; notify(); };
    const key = event => { if (event.key === 'Escape') receiver.invokeMethodAsync('Dismiss').catch(() => {}); };
    parent.addEventListener('mouseenter', enter); parent.addEventListener('mouseleave', leave);
    parent.addEventListener('focusin', focus); parent.addEventListener('focusout', blur); parent.addEventListener('keydown', key);
    parents.set(trigger, { parent, enter, leave, focus, blur, key, describedBy, addedDescription });
    if (focused) notify();
}

export function disposeTooltipParent(trigger) {
    const state = parents.get(trigger);
    if (!state) return;
    state.parent.removeEventListener('mouseenter', state.enter); state.parent.removeEventListener('mouseleave', state.leave);
    state.parent.removeEventListener('focusin', state.focus); state.parent.removeEventListener('focusout', state.blur);
    state.parent.removeEventListener('keydown', state.key);
    if (state.addedDescription) {
        const remaining = (state.parent.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(value => value && value !== state.describedBy);
        if (remaining.length) state.parent.setAttribute('aria-describedby', remaining.join(' ')); else state.parent.removeAttribute('aria-describedby');
    }
    parents.delete(trigger);
}
