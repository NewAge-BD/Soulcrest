// Separate, read-only dungeon scenes. No world-map, tracking or navigation state is shared.
window.soulcrestDungeonMaps = (() => {
    'use strict';

    const instances = new Map();
    const dialogs = new Map();

    function notify(dotnet, method, ...args) {
        if (!dotnet || typeof dotnet.invokeMethodAsync !== 'function') return;
        try {
            Promise.resolve(dotnet.invokeMethodAsync(method, ...args)).catch(() => {});
        } catch (_) { /* A disposed Blazor owner must not break map cleanup. */ }
    }

    const keyOf = scene => typeof scene?.key === 'string' ? scene.key : '';
    const current = (entry, revision) => !entry.disposed && entry.revision === revision
        && instances.get(entry.id) === entry;

    function imageSource(value) {
        if (typeof value !== 'string' || !value.trim() || value.includes('\\')) return null;
        try {
            const page = new URL(window.location.href);
            const source = value.trim();
            if (source.startsWith('//')) return null;
            const url = new URL(source, document.baseURI || page.href);
            if (url.username || url.password) return null;
            // file: is solely an offline-preview capability, never enabled by production scenes.
            if (url.protocol === 'file:') {
                return page.protocol === 'file:' && !url.hostname && /^\/[a-z]:\//i.test(url.pathname)
                    ? url.href : null;
            }
            if (url.protocol !== 'https:') return null;
            if (url.hostname === 'mapdata.soulcrest' && !url.port) return url.href;
            const relative = !/^[a-z][a-z\d+.-]*:/i.test(source);
            return relative && url.origin === page.origin ? url.href : null;
        } catch (_) { return null; }
    }

    function validScene(scene) {
        return scene && typeof scene.key === 'string' && scene.key.length > 0
            && Number.isFinite(scene.width) && scene.width > 0
            && Number.isFinite(scene.height) && scene.height > 0 && Array.isArray(scene.pins);
    }

    function pinsOf(scene) {
        return scene.pins.filter(pin => pin && typeof pin.id === 'string' && pin.id.length > 0
            && Number.isInteger(pin.petNumber) && pin.petNumber > 0
            && Number.isFinite(pin.x) && Number.isFinite(pin.y)
            && pin.x >= 0 && pin.x <= scene.width && pin.y >= 0 && pin.y <= scene.height);
    }

    function clearImage(entry) {
        const layer = entry.image;
        if (!layer) return;
        entry.image = null;
        layer.off('load', entry.onLoad);
        layer.off('error', entry.onError);
        layer.remove();
        entry.onLoad = entry.onError = null;
    }

    function tooltipFor(pin) {
        const box = document.createElement('div');
        const name = document.createElement('strong');
        name.textContent = pin.petName || `Pet ${pin.petNumber}`;
        box.appendChild(name);
        if (pin.npcName && pin.npcName !== pin.petName) {
            box.appendChild(document.createElement('br'));
            const npc = document.createElement('span');
            npc.textContent = pin.npcName;
            box.appendChild(npc);
        }
        return box;
    }

    function drawPins(entry) {
        entry.pins.clearLayers();
        // Selected points are painted last and in their own upper pane.
        for (const pin of [...entry.validPins].sort((a, b) => Number(a.selected === true) - Number(b.selected === true))) {
            const selected = pin.selected === true;
            const marker = L.circleMarker([-pin.y, pin.x], {
                pane: selected ? 'dungeon-selected-pets' : 'dungeon-pets',
                radius: selected ? 8 : 4.5,
                color: selected ? '#f6f0ff' : '#b5a1c5',
                weight: selected ? 2.5 : 1.25,
                fillColor: selected ? '#bd83f3' : '#8e72a7',
                fillOpacity: selected ? 0.95 : 0.7,
                opacity: selected ? 1 : 0.85
            }).bindTooltip(tooltipFor(pin), { direction: 'top', offset: [0, -8] }).addTo(entry.pins);
            if (selected) marker.bringToFront();
            const element = marker.getElement?.();
            if (element) {
                element.setAttribute('tabindex', '0');
                element.setAttribute('role', 'img');
                element.setAttribute('aria-label', [pin.petName || `Pet ${pin.petNumber}`, pin.npcName].filter(Boolean).join(': '));
                element.addEventListener('focus', () => marker.openTooltip());
                element.addEventListener('blur', () => marker.closeTooltip());
            }
        }
    }

    function fitSelected(elementId) {
        const entry = instances.get(elementId);
        if (!entry || !entry.ready) return 0;
        const selected = entry.validPins.filter(pin => pin.selected === true);
        const pins = selected.length ? selected : entry.validPins;
        if (pins.length) {
            entry.map.fitBounds(L.latLngBounds(pins.map(pin => [-pin.y, pin.x])), {
                padding: [32, 32], maxZoom: 1, animate: false
            });
        } else {
            entry.map.fitBounds(entry.bounds, { padding: [12, 12], maxZoom: 0, animate: false });
        }
        return pins.length;
    }

    function setScene(elementId, scene) {
        const entry = instances.get(elementId);
        if (!entry) return false;
        const revision = ++entry.revision;
        entry.ready = false;
        entry.validPins = [];
        clearImage(entry);
        entry.pins.clearLayers();
        const fail = reason => {
            if (current(entry, revision)) notify(entry.dotnet, 'OnDungeonMapError', keyOf(scene), reason);
            return false;
        };
        if (!validScene(scene)) return fail('invalid-scene');
        const source = imageSource(scene.imageUrl);
        if (!source) return fail('invalid-image-url');
        entry.scene = scene;
        entry.validPins = pinsOf(scene);
        entry.bounds = L.latLngBounds([[-scene.height, 0], [0, scene.width]]);
        entry.map.setMaxBounds(entry.bounds.pad(0.15));
        // Establish the initial view before ImageOverlay.addTo; Leaflet waits for a loaded map.
        entry.map.fitBounds(entry.bounds, { padding: [12, 12], maxZoom: 0, animate: false });
        let completed = false;
        const layer = L.imageOverlay(source, entry.bounds, { interactive: false, alt: '' });
        entry.image = layer;
        const active = () => !completed && current(entry, revision) && entry.image === layer;
        entry.onLoad = () => {
            if (!active()) return;
            completed = true;
            entry.ready = true;
            drawPins(entry);
            entry.map.invalidateSize({ animate: false, pan: false });
            fitSelected(elementId);
            notify(entry.dotnet, 'OnDungeonMapReady', scene.key);
        };
        entry.onError = () => {
            if (!active()) return;
            completed = true;
            entry.ready = false;
            entry.validPins = [];
            entry.pins.clearLayers();
            clearImage(entry);
            fail('image-unavailable');
        };
        layer.on('load', entry.onLoad).on('error', entry.onError).addTo(entry.map);
        return true;
    }

    function dispose(elementId) {
        const entry = instances.get(elementId);
        if (!entry) return;
        instances.delete(elementId);
        entry.disposed = true;
        ++entry.revision;
        entry.observer?.disconnect();
        clearImage(entry);
        entry.pins.clearLayers();
        entry.map.remove();
    }

    function create(elementId, scene, dotnet = null) {
        dispose(elementId);
        if (!document.getElementById(elementId)) {
            notify(dotnet, 'OnDungeonMapError', keyOf(scene), 'container-unavailable');
            return false;
        }
        if (typeof L === 'undefined' || !L.map || !L.CRS?.Simple) {
            notify(dotnet, 'OnDungeonMapError', keyOf(scene), 'map-unavailable');
            return false;
        }
        const map = L.map(elementId, {
            crs: L.CRS.Simple, minZoom: -8, maxZoom: 2, zoomSnap: 0.25, zoomDelta: 0.5,
            attributionControl: false, fadeAnimation: false, zoomAnimation: false
        });
        map.createPane('dungeon-pets').style.zIndex = '600';
        map.createPane('dungeon-selected-pets').style.zIndex = '650';
        const entry = {
            id: elementId, map, dotnet, pins: L.layerGroup().addTo(map), image: null,
            revision: 0, disposed: false, ready: false, validPins: [], observer: null
        };
        instances.set(elementId, entry);
        if (typeof ResizeObserver !== 'undefined') {
            entry.observer = new ResizeObserver(() => {
                if (instances.get(elementId) === entry && !entry.disposed)
                    map.invalidateSize({ animate: false, pan: false });
            });
            entry.observer.observe(document.getElementById(elementId));
        }
        return setScene(elementId, scene);
    }

    function openDialog(dialogId, returnButtonId, dotnet = null) {
        const dialog = document.getElementById(dialogId);
        if (!dialog || typeof dialog.showModal !== 'function') return false;
        const existing = dialogs.get(dialogId);
        if (existing) {
            existing.dotnet = dotnet;
            existing.returnTo = document.getElementById(returnButtonId) || existing.returnTo;
            return true;
        }
        const entry = { dialog, dotnet, returnTo: document.getElementById(returnButtonId) || document.activeElement };
        const onClose = () => {
            if (dialogs.get(dialogId) !== entry || dialog.open) return;
            dialogs.delete(dialogId);
            dialog.removeEventListener('close', onClose);
            if (entry.returnTo?.isConnected) entry.returnTo.focus();
            notify(entry.dotnet, 'OnDungeonDialogClosed');
        };
        entry.onClose = onClose;
        dialogs.set(dialogId, entry);
        dialog.addEventListener('close', onClose);
        try {
            // Native modal dialogs provide focus containment and Escape handling.
            if (!dialog.open) dialog.showModal();
            return true;
        } catch (_) {
            dialog.removeEventListener('close', onClose);
            dialogs.delete(dialogId);
            return false;
        }
    }

    function closeDialog(dialogId) {
        const dialog = document.getElementById(dialogId);
        if (dialog?.open && typeof dialog.close === 'function') dialog.close();
        // Native close events are queued. Finish now so an immediate reopen cannot reuse a closed dialog.
        dialogs.get(dialogId)?.onClose();
    }

    return { create, setScene, dispose, fitSelected, openDialog, closeDialog };
})();
