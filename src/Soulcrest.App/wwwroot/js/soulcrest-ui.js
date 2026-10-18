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
