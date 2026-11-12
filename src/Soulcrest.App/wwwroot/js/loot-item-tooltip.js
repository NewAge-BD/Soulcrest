// SVG item markers cannot host an HTML tooltip component. Share the bundled foreground
// projected by Razor, keeping the SVG frame palette and keyboard targets independent.
(() => {
    let anchor = null, bubble = null;
    const selector = '[data-loot-item-tooltip="true"]';
    function hide() {
        if (anchor && bubble) anchor.removeAttribute('aria-describedby');
        bubble?.remove(); bubble = null; anchor = null;
    }
    function show(target) {
        if (target === anchor) return;
        hide(); anchor = target;
        const text = target.dataset.itemNameText;
        if (!text) return hide();
        bubble = document.createElement('div');
        bubble.id = 'soulcrest-route-item-tooltip';
        bubble.className = 'item-tooltip-bubble route-item-tooltip';
        bubble.setAttribute('role', 'tooltip');
        const name = document.createElement('span');
        name.className = 'item-name'; name.textContent = text;
        const color = target.dataset.itemNameColor;
        if (/^#[0-9A-F]{6}$/.test(color || '')) name.style.color = color;
        bubble.append(name, document.createTextNode(' × ' + (target.dataset.itemQuantityText || '?')));
        document.body.append(bubble);
        target.setAttribute('aria-describedby', bubble.id);
        const box = target.getBoundingClientRect(), tip = bubble.getBoundingClientRect();
        const left = Math.max(8, Math.min(box.left, window.innerWidth - tip.width - 8));
        const below = box.bottom + 8;
        const top = below + tip.height < window.innerHeight - 8 ? below : Math.max(8, box.top - tip.height - 8);
        Object.assign(bubble.style, { left: left + 'px', top: top + 'px' });
    }
    document.addEventListener('pointerover', event => {
        const target = event.target.closest?.(selector);
        if (target) show(target);
    });
    document.addEventListener('pointerout', event => {
        if (anchor && event.target.closest?.(selector) === anchor && !anchor.contains(event.relatedTarget)) hide();
    });
    document.addEventListener('focusin', event => {
        const target = event.target.closest?.(selector);
        if (target) show(target);
    });
    document.addEventListener('focusout', event => { if (event.target === anchor) hide(); });
    document.addEventListener('keydown', event => { if (event.key === 'Escape') hide(); });
    document.addEventListener('scroll', hide, true);
    window.addEventListener('resize', hide);
    if (typeof MutationObserver !== 'undefined') new MutationObserver(() => {
        if (anchor && !anchor.isConnected) hide();
    }).observe(document.body, { childList: true, subtree: true });
})();
