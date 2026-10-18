// Image markers share Leaflet's canvas, hit testing and popup/event routing.
// No image or HTML element is created for an individual spawn.
window.soulcrestCanvasIcons = (() => {
    const images = new Map();
    function imageFor(url, layer) {
        let entry = images.get(url);
        if (!entry) {
            const image = new Image();
            entry = { image, waiting: new Set(), sprites: new Map() };
            images.set(url, entry);
            image.onload = image.onerror = () => {
                for (const marker of entry.waiting) if (marker._map) marker.redraw();
                entry.waiting.clear();
            };
            image.src = url;
        }
        if (!entry.image.complete) entry.waiting.add(layer);
        return entry;
    }
    // Reuse the padded bitmap while dragging within it. Leaflet's map pane supplies the
    // translation; redraw only when the visible viewport leaves the cached canvas or zoom changes.
    const BufferedCanvas = L.Canvas.extend({
        _update() {
            if (this._bufferContainer === this._container && this._bounds && this._zoom === this._map.getZoom()) {
                const start = this._map.containerPointToLayerPoint([0, 0]);
                const end = start.add(this._map.getSize());
                if (this._bounds.contains(L.bounds(start, end))) return;
            }
            L.Canvas.prototype._update.call(this);
            this._bufferContainer = this._container;
        },
        _reset() {
            this._bufferContainer = null;
            L.Canvas.prototype._reset.call(this);
        },
    });
    const Icon = L.CircleMarker.extend({
        initialize(latlng, options) {
            L.CircleMarker.prototype.initialize.call(this, latlng, { ...options, radius: 12, stroke: false });
            this._imageEntry = imageFor(options.url, this);
            this.refreshAppearance();
        },
        refreshAppearance() {
            const state = this.options.appearance();
            this._appearance = state;
            // Radius also drives Canvas redraw bounds and Leaflet's pointer hit testing.
            this.setRadius(12 * state.scale + 8);
            return this;
        },
        setIcon() { return this.refreshAppearance(); },
        onRemove(map) {
            this._imageEntry.waiting.delete(this);
            L.CircleMarker.prototype.onRemove.call(this, map);
        },
        onAdd(map) {
            if (!this._imageEntry.image.complete) this._imageEntry.waiting.add(this);
            L.CircleMarker.prototype.onAdd.call(this, map);
        },
        _updatePath() {
            const renderer = this._renderer;
            if (!renderer._drawing || this._empty()) return;
            const ctx = renderer._ctx, p = this._point, state = this.options.appearance();
            const size = 24 * state.scale, image = this._imageEntry.image;
            ctx.save();
            ctx.globalAlpha = state.kibelisk ? (state.done ? 1 : 0.75) : (state.done ? 0.45 : 1);
            if (state.kibelisk && !state.done) ctx.filter = 'grayscale(1) brightness(.8)';
            if (image.complete && image.naturalWidth > 0) {
                // Resize each source once per zoom size, rather than for every spawn on every frame.
                const pixels = Math.ceil(size * Math.min(2, window.devicePixelRatio || 1));
                let sprite = this._imageEntry.sprites.get(pixels);
                if (!sprite) {
                    sprite = document.createElement('canvas'); sprite.width = sprite.height = pixels;
                    sprite.getContext('2d').drawImage(image, 0, 0, pixels, pixels);
                    // Zoom has a continuous animation: keep the cache bounded per source.
                    if (this._imageEntry.sprites.size >= 8) this._imageEntry.sprites.delete(this._imageEntry.sprites.keys().next().value);
                    this._imageEntry.sprites.set(pixels, sprite);
                }
                ctx.drawImage(sprite, p.x - size / 2, p.y - size / 2, size, size);
            } else {
                // Keep the marker visible and selectable even for a missing local icon.
                ctx.fillStyle = '#94a3b8'; ctx.beginPath(); ctx.arc(p.x, p.y, 4, 0, Math.PI * 2); ctx.fill();
            }
            ctx.filter = 'none'; ctx.globalAlpha = 1;
            if (state.color) {
                ctx.strokeStyle = state.color; ctx.lineWidth = 2;
                ctx.beginPath(); ctx.arc(p.x, p.y, size / 2, 0, Math.PI * 2); ctx.stroke();
            }
            if (state.done && !state.kibelisk) {
                ctx.fillStyle = '#167c4d'; ctx.fillRect(p.x + size / 2 - 8, p.y - size / 2 - 3, 14, 14);
                ctx.fillStyle = '#fff'; ctx.font = 'bold 12px sans-serif';
                ctx.fillText('\u2713', p.x + size / 2 - 7, p.y - size / 2 + 8);
            }
            ctx.restore();
        },
    });
    return { renderer: options => new BufferedCanvas(options), create: (latlng, options) => new Icon(latlng, options), redraw(renderer) {
        if (!renderer._map || !renderer._ctx) return;
        renderer._redrawBounds = renderer._bounds;
        renderer._redrawRequest = renderer._redrawRequest || L.Util.requestAnimFrame(renderer._redraw, renderer);
    } };
})();
