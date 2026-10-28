(function () {
    "use strict";
    const attached = new WeakMap();
    const captures = new WeakMap();
    const trackKeys = new Set(["+", "=", "-", "ArrowLeft", "ArrowRight", "Home", " ", "Spacebar"]);
    const headKeys = new Set(["ArrowLeft", "ArrowRight", "Home", "End", " ", "Spacebar"]);
    function bounds(element) {
        const rect = element.getBoundingClientRect();
        return [rect.width, rect.left];
    }
    function detach(element) {
        const state = attached.get(element);
        if (!state) return;
        element.removeEventListener("wheel", state.wheel);
        element.removeEventListener("keydown", state.key);
        element.removeEventListener("pointerdown", state.down);
        element.removeEventListener("pointerup", state.end);
        element.removeEventListener("pointercancel", state.end);
        state.observer?.disconnect();
        attached.delete(element);
    }
    function attach(element, callback) {
        detach(element);
        const wheel = event => {
            // Ordinary scrolling and native controls keep their normal browser behavior.
            if (event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) event.preventDefault();
        };
        const key = event => {
            if (event.ctrlKey || event.altKey || event.metaKey) return;
            const ownTrack = event.target === element;
            const ownHead = event.target?.closest?.("[data-playhead-grip]");
            if ((ownTrack && trackKeys.has(event.key)) || (ownHead && element.contains(ownHead) && headKeys.has(event.key))) event.preventDefault();
        };
        let nativePointer = null;
        const down = event => {
            if (event.button !== 0 || event.isPrimary === false || nativePointer !== null) return;
            const head = event.target?.closest?.("[data-playhead-grip]");
            if (!head && event.target?.closest?.("button,input,select,textarea,a,[contenteditable]")) return;
            const target = head && element.contains(head) ? head : element;
            nativePointer = event.pointerId;
            // Capture in the native event, before the asynchronous Blazor/JS measurement round trip.
            capture(target, event.pointerId);
        };
        const end = event => { if (nativePointer === event.pointerId) nativePointer = null; };
        element.addEventListener("wheel", wheel, { passive: false });
        element.addEventListener("keydown", key);
        element.addEventListener("pointerdown", down);
        element.addEventListener("pointerup", end);
        element.addEventListener("pointercancel", end);
        const notify = () => {
            if (!callback || attached.get(element)?.callback !== callback) return;
            const [width, left] = bounds(element);
            // A disposed Blazor component may race its last resize notification.
            Promise.resolve(callback.invokeMethodAsync("TimelineResized", width, left)).catch(() => {});
        };
        const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(notify);
        attached.set(element, { wheel, key, down, end, observer, callback });
        observer?.observe(element);
    }
    function capture(element, pointerId, generation) {
        if (generation !== undefined) {
            const previous = captures.get(element);
            if (previous && (previous.generation > generation || previous.generation === generation && !previous.active)) return;
            captures.set(element, { generation, pointerId, active: true });
        }
        element.focus({ preventScroll: true });
        try { element.setPointerCapture(pointerId); } catch (_) { /* Pointer may already have ended. */ }
    }
    function release(element, pointerId, generation) {
        if (generation !== undefined) {
            const previous = captures.get(element);
            if (previous && previous.generation > generation) return;
            captures.set(element, { generation, pointerId, active: false });
        }
        if (element.hasPointerCapture(pointerId)) element.releasePointerCapture(pointerId);
    }
    window.soulcrestLootTimeline = { attach, detach, bounds, capture, release };
}());
