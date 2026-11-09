// Small helpers for the Razor views.
window.soulcrestUi = {
    // Keep footer/search selections visible without moving keyboard focus or the release text.
    revealPatchnotesVersion(list) {
        const active = list?.querySelector('.pn-version[aria-current="true"]');
        if (!active || !list.clientHeight) return;
        const bounds = list.getBoundingClientRect();
        const item = active.getBoundingClientRect();
        if (item.top < bounds.top + 4) list.scrollTop += item.top - bounds.top - 4;
        else if (item.bottom > bounds.bottom - 4) list.scrollTop += item.bottom - bounds.bottom + 4;
    },
    // Blazor sets "muted" only as an attribute, which does not mute a video created later, so WebView2
    // blocks its autoplay. Muting the element itself lets the tutorial video play in a loop.
    playMuted(video) {
        if (!video) return false;
        video.muted = true;
        const playing = video.play();
        if (playing) playing.catch(() => {});
        return true;
    },
};

// A version selected in the narrow layout must remain visible when the desktop list returns.
window.addEventListener('resize', () => {
    const list = document.querySelector('.patchnotes .pn-version-list');
    if (list) window.soulcrestUi.revealPatchnotesVersion(list);
});

// User request 2026-11-09: the whole compact boss card opens its existing settings disclosure.
// Forward to the summary so Blazor keeps the expanded state across timer refreshes.
document.addEventListener('click', event => {
    if (event.button !== 0 || event.defaultPrevented) return;
    const target = event.target instanceof Element ? event.target : event.target?.parentElement;
    const card = target?.closest('.boss-timers-compact .boss-timer-card');
    if (!card || target.closest('a,button,input,select,textarea,label,summary,[role="button"],[contenteditable="true"]')) return;
    if (window.getSelection()?.toString()) return;
    card.querySelector('.boss-timer-options > summary')?.click();
});

// A normal mouse wheel can reach every main tab as well as the visible scrollbar and touchpad.
document.addEventListener('wheel', event => {
    if (event.ctrlKey || event.defaultPrevented || Math.abs(event.deltaX) >= Math.abs(event.deltaY)) return;
    const target = event.target instanceof Element ? event.target : event.target?.parentElement;
    const tabs = target?.closest('.topbar .tabs');
    if (!tabs || tabs.scrollWidth <= tabs.clientWidth) return;
    const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? tabs.clientWidth : 1;
    const previous = tabs.scrollLeft;
    tabs.scrollLeft += event.deltaY * unit;
    if (tabs.scrollLeft !== previous) event.preventDefault();
}, { passive: false });
