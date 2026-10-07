// Small helpers for the Razor views.
window.soulcrestUi = {
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
