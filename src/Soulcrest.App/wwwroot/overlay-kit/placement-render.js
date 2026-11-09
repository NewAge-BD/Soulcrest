(() => {
    "use strict";
    const rootId = "overlay-placement-render-surface";
    let active = null;
    const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
    const bounded = promise => new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error("Editor assets did not become ready.")), 6000);
        Promise.resolve(promise).then(value => { clearTimeout(timer); resolve(value); }, error => { clearTimeout(timer); reject(error); });
    });
    window.soulcrestPlacementRender = {
        begin(token) {
            const request = { token, state: "waiting" };
            active = request;
            (async () => {
                const start = performance.now();
                let root;
                while (!(root = document.getElementById(rootId)) || root.dataset.renderToken !== token) {
                    if (active !== request) return;
                    if (performance.now() - start > 6000) throw new Error("Editor surface did not arrive.");
                    await frame();
                }
                await bounded(document.fonts.ready);
                const images = Array.from(root.querySelectorAll("img"));
                images.forEach(image => { image.loading = "eager"; });
                await bounded(Promise.all(images.map(image => image.decode().catch(() => {}))));
                await bounded((async () => {
                    // A failed AssetImage is replaced by its production Razor fallback over the local bridge.
                    while (Array.from(root.querySelectorAll("img")).some(image => !image.complete || image.naturalWidth === 0)) await frame();
                })());
                if (active !== request) return;
                window.soulcrestOverlayEditor.mount(rootId, null, true);
                await frame(); await frame(); await frame();
                if (active !== request || root.dataset.renderToken !== token) return;
                const bounds = root.querySelector(".oe-stage").getBoundingClientRect();
                Object.assign(request, { state: "ready", density: window.devicePixelRatio,
                    viewportWidth: window.innerWidth, viewportHeight: window.innerHeight,
                    left: bounds.left, top: bounds.top, width: bounds.width, height: bounds.height });
            })().catch(error => { if (active === request) Object.assign(request, { state: "error", error: String(error.message || error) }); });
            return true;
        },
        status(token) { return active?.token === token ? active : null; }
    };
})();
