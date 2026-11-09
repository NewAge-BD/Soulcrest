/* Grindcrest overlay-editor-kit 1.15.0; source 231a2f0d40dcba872a2a83aa86b8f7b7a1edf49d. */
(() => {
    "use strict";
    const editors = new Map();
    const number = (element, name) => Number(element.dataset[name]) || 0;
    const clamp = (value, min, max) => Math.min(Math.max(value, min), Math.max(min, max));

    window.soulcrestOverlayEditor = {
        mount(id, dotnet, readOnly = false) {
            this.unmount(id);
            const root = document.getElementById(id);
            if (!root) return;
            const viewport = root.querySelector(".oe-stage-viewport");
            const wrap = root.querySelector(".oe-stage-wrap");
            const stage = root.querySelector(".oe-stage");
            const inspector = root.querySelector(".oe-inspector");
            const guideLayer = stage.querySelector(".oe-alignment-guides");
            let drag = null, ghost = null, suppressClick = false, disposed = false, contentFrame = null;
            const invoke = (name, ...args) => {
                if (disposed) return;
                dotnet.invokeMethodAsync(name, ...args).catch(() => {});
            };
            const fit = () => {
                if (drag && drag.overlayId !== stage.dataset.overlayId) cleanupDrag();
                if (drag && drag.moved) {
                    // Live snapshots may replace text/styles while the pointer
                    // rests mid-drag. Reapply only the pending content geometry;
                    // the stage and unsaved widget rectangles must stay put.
                    stage.querySelectorAll(".oe-widget").forEach(element => {
                        if (element === drag.element && drag.type === "resize")
                            resizeContent(element, drag.newWidth ?? drag.width, drag.newHeight ?? drag.height);
                        else resizeContent(element, number(element, "width"), number(element, "height"));
                    });
                    scheduleContentFit();
                    return;
                }
                const style = getComputedStyle(viewport);
                const available = viewport.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
                const width = number(stage, "width") + number(stage, "chromeX"), height = number(stage, "height") + number(stage, "chromeY");
                const scale = Math.max(.2, Math.min(1, (available - 8) / Math.max(1, width)));
                stage.style.transform = `scale(${scale})`;
                wrap.style.width = `${width * scale}px`;
                wrap.style.height = `${height * scale}px`;
                stage.querySelectorAll(".oe-widget").forEach(element => resizeContent(element, number(element, "width"), number(element, "height")));
            };
            const fitText = line => {
                line.style.setProperty("--line-fit", "1");
                // List names keep the configured row font. Their full text remains on the item tooltip.
                if (line.classList.contains("overlay-item-name") && line.closest(".overlay-widget-items.is-list")) return;
                const style = getComputedStyle(line);
                if (!line.getClientRects().length || style.display === "none" || style.clipPath !== "none") return;
                const bounds = line.getBoundingClientRect();
                if (bounds.width <= 0 || bounds.height <= 0) return;
                const range = document.createRange();
                range.selectNodeContents(line);
                const rectangles = Array.from(range.getClientRects()).filter(rect => rect.width > 0 && rect.height > 0);
                line.querySelectorAll(".icon,.overlay-status-dot").forEach(icon => rectangles.push(icon.getBoundingClientRect()));
                if (!rectangles.length) return;
                const left = Math.min(...rectangles.map(rect => rect.left)), right = Math.max(...rectangles.map(rect => rect.right));
                const top = Math.min(...rectangles.map(rect => rect.top)), bottom = Math.max(...rectangles.map(rect => rect.bottom));
                const visualScale = bounds.width / (parseFloat(style.width) || line.offsetWidth || 1);
                const horizontalInset = parseFloat(style.paddingLeft) + parseFloat(style.paddingRight) +
                    parseFloat(style.borderLeftWidth) + parseFloat(style.borderRightWidth);
                const verticalInset = parseFloat(style.paddingTop) + parseFloat(style.paddingBottom) +
                    parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth);
                const width = Math.max(.01, bounds.width - horizontalInset * visualScale);
                const height = Math.max(.01, bounds.height - verticalInset * visualScale);
                const factor = Math.max(.001, Math.min(1, width / Math.max(.01, right - left), height / Math.max(.01, bottom - top)));
                if (factor < .999) line.style.setProperty("--line-fit", String(factor * .995));
            };
            const fitContentText = () => {
                contentFrame = null;
                if (disposed) return;
                // User request 2026-11-06: prevent drop-count-dependent list font shrinking.
                // The server caps lists to complete rows. During a pending resize,
                // clip complete rows at their configured size instead of shrinking the list.
                // Other views retain the original fit of their previous arrangement.
                stage.querySelectorAll(".overlay-loot-viewport").forEach(viewport => {
                    const items = viewport.querySelector(".overlay-widget-items");
                    if (!items) return;
                    const style = getComputedStyle(items);
                    const width = parseFloat(style.getPropertyValue("--loot-layout-width"));
                    const height = parseFloat(style.getPropertyValue("--loot-layout-height"));
                    if (items.classList.contains("is-list")) {
                        items.style.transform = "none";
                        const rowHeight = parseFloat(style.getPropertyValue("--loot-cell-height"));
                        const gap = Math.max(0, parseFloat(style.getPropertyValue("--loot-gap")) || 0);
                        const measuredHeight = parseFloat(getComputedStyle(viewport).height);
                        const availableHeight = Number.isFinite(measuredHeight) ? measuredHeight : viewport.clientHeight;
                        const rows = rowHeight > 0 ? Math.max(0, Math.floor((availableHeight + gap + .02) / (rowHeight + gap))) : 0;
                        const shownHeight = rows * rowHeight + Math.max(0, rows - 1) * gap;
                        items.style.clipPath = rowHeight > 0 && height > 0
                            ? `inset(0 0 ${Math.max(0, height - shownHeight)}px 0)` : "none";
                        return;
                    }
                    items.style.clipPath = "";
                    const scale = width > 0 && height > 0
                        ? Math.min(1, viewport.clientWidth / width, viewport.clientHeight / height) : 1;
                    items.style.transform = `scale(${Math.max(.001, scale)})`;
                });
                stage.querySelectorAll("[data-overlay-fit]").forEach(fitText);
            };
            const scheduleContentFit = () => {
                if (contentFrame === null && !disposed) contentFrame = requestAnimationFrame(fitContentText);
            };
            const resizeContent = (element, width, height) => {
                const viewport = element.querySelector(".overlay-widget-viewport");
                const content = viewport?.querySelector(".overlay-widget-preview");
                if (!content || width <= 0 || height <= 0) return;
                const referenceWidth = number(viewport, "contentWidth") || width;
                const referenceHeight = number(viewport, "contentHeight") || height;
                const minimumWidth = number(viewport, "minContentWidth") || 80;
                const minimumHeight = number(viewport, "minContentHeight") || 48;
                const scale = viewport.dataset.reflow === "true" ? Math.min(1, width / minimumWidth, height / minimumHeight) : Math.min(width / referenceWidth, height / referenceHeight,
                    width / minimumWidth, height / minimumHeight);
                content.style.width = `${width / scale}px`;
                content.style.height = `${height / scale}px`;
                content.style.transform = `scale(${scale})`;
                if (viewport.dataset.reflow === "true") {
                    const count = Math.max(1, number(viewport, "gridCount"));
                    const size = number(viewport, "gridSize") || 56;
                    const font = number(viewport, "gridFont") || 1;
                    const header = number(viewport, "gridHeader") * font;
                    const footer = number(viewport, "gridFooter") * font;
                    const innerWidth = Math.max(0, width / scale - 20);
                    const innerHeight = Math.max(0, height / scale - 16);
                    let columns = Math.max(1, Math.min(count, Math.floor((innerWidth + 4) / (size + 4))));
                    const fit = cols => Math.min(1, innerWidth / (cols * size + (cols - 1) * 4),
                        innerHeight / (header + footer + Math.ceil(count / cols) * size + (Math.ceil(count / cols) - 1) * 4));
                    let cellScale = fit(columns);
                    for (let cols = 1; cellScale < 1 && cols <= count; cols++) {
                        const candidate = fit(cols);
                        if (candidate > cellScale) { columns = cols; cellScale = candidate; }
                    }
                    const rows = Math.ceil(count / columns);
                    const values = {
                        "--widget-font-scale": font * cellScale,
                        "--loot-item-size": `${size * cellScale}px`, "--loot-gap": `${4 * cellScale}px`,
                        "--loot-columns": columns, "--loot-cell-width": `${size * cellScale}px`,
                        "--loot-cell-height": `${size * cellScale}px`, "--loot-header-height": `${header * cellScale}px`,
                        "--loot-footer-height": `${footer * cellScale}px`,
                        "--loot-layout-width": `${(columns * size + (columns - 1) * 4) * cellScale}px`,
                        "--loot-layout-height": `${(rows * size + (rows - 1) * 4) * cellScale}px`
                    };
                    for (const [key, value] of Object.entries(values)) content.style.setProperty(key, value);
                    content.querySelectorAll(".overlay-widget-item").forEach(item => {
                        const length = item.querySelector(".overlay-item-quantity")?.textContent.length || 1;
                        item.style.setProperty("--loot-count-size", `${Math.max(.1, Math.min(13 * font * cellScale, Math.max(1, (size - 6) * cellScale) / (length * .65)))}px`);
                    });
                }
                scheduleContentFit();
            };
            const snapshot = () => ({ width: number(stage, "width"), height: number(stage, "height"),
                chromeX: number(stage, "chromeX"), chromeY: number(stage, "chromeY"), rect: stage.getBoundingClientRect() });
            const contentRect = () => number(stage, "chromeY") > 0
                ? stage.querySelector(".oe-stage-content").getBoundingClientRect() : stage.getBoundingClientRect();
            const grid = value => stage.dataset.snap === "true" ? Math.round(value / 8) * 8 : Math.round(value);
            const showGuides = guides => {
                if (!guideLayer) return;
                guideLayer.replaceChildren();
                guideLayer.style.setProperty("--guide-scale", String(1 / (drag?.scale || 1)));
                for (const guide of guides) {
                    const line = document.createElement("span");
                    const vertical = guide.x1 === guide.x2;
                    line.className = `oe-alignment-guide ${vertical ? "is-vertical" : "is-horizontal"} ${guide.kind === "gap" ? "is-gap" : ""}`;
                    line.style.left = `${Math.min(guide.x1, guide.x2)}px`;
                    line.style.top = `${Math.min(guide.y1, guide.y2)}px`;
                    line.style.width = `${Math.abs(guide.x2 - guide.x1)}px`;
                    line.style.height = `${Math.abs(guide.y2 - guide.y1)}px`;
                    if (guide.kind === "gap" && Number.isFinite(guide.value)) {
                        const label = document.createElement("span");
                        label.textContent = `${guide.value.toLocaleString(document.documentElement?.lang || undefined, { maximumFractionDigits: 1 })} px`;
                        line.appendChild(label);
                    }
                    guideLayer.appendChild(line);
                }
            };
            const align = (rect, mode, e) => {
                const minimum = { width: Math.min(80, drag.width), height: Math.min(40, drag.height) };
                if (stage.dataset.autoAlign === "true" && !e.altKey && window.soulcrestOverlayAlignment
                    && rect.width > 0 && rect.height > 0) {
                    const result = window.soulcrestOverlayAlignment.snap({ rect, peers: drag.peers, mode,
                        tolerance: Math.min(24, 6 / drag.scale), bounds: { width: 1600, height: 1200 },
                        minimum, grid: stage.dataset.snap === "true" ? 8 : 0 });
                    showGuides(result.guides);
                    return result;
                }
                showGuides([]);
                return mode === "resize"
                    ? { ...rect, width: clamp(grid(rect.width), minimum.width, 1600 - rect.x),
                        height: clamp(grid(rect.height), minimum.height, 1200 - rect.y) }
                    : { ...rect, x: clamp(grid(rect.x), 0, 1600 - rect.width), y: clamp(grid(rect.y), 0, 1200 - rect.height) };
            };
            const restoreWidget = widget => {
                widget.element.style.left = `${widget.x}px`;
                widget.element.style.top = `${widget.y}px`;
                widget.element.style.width = `${widget.width}px`;
                widget.element.style.height = `${widget.height}px`;
                resizeContent(widget.element, widget.width, widget.height);
            };
            const restore = current => {
                if (current.element) {
                    restoreWidget(current);
                } else if (current.type === "canvas") {
                    stage.style.width = `${current.canvas.width + current.canvas.chromeX}px`;
                    stage.style.height = `${current.canvas.height + current.canvas.chromeY}px`;
                    stage.querySelectorAll(".oe-widget").forEach(element => {
                        element.style.left = `${number(element,"x")}px`;
                        element.style.top = `${number(element,"y")}px`;
                    });
                }
            };
            const cleanupDrag = () => {
                showGuides([]);
                wrap.style.position = ""; wrap.style.left = ""; wrap.style.top = "";
                document.body.classList.remove("oe-resizing-reverse");
                ghost?.remove(); ghost = null;
                stage.classList.remove("is-drop-target");
                document.body.classList.remove("oe-dragging", "oe-resizing");
                if (drag?.capture.hasPointerCapture?.(drag.pointerId)) drag.capture.releasePointerCapture(drag.pointerId);
                drag = null;
            };
            const down = e => {
                if (e.button !== 0 || drag || e.target.closest("[data-widget-delete]")) return;
                const module = e.target.closest("[data-module-kind]");
                const grip = e.target.closest("[data-widget-drag]");
                const resize = e.target.closest("[data-widget-resize]");
                const canvasResize = e.target.closest("[data-canvas-resize]");
                const capture = module || grip || resize || canvasResize;
                if (!capture || capture.disabled || !root.contains(capture)) return;
                const canvas = snapshot();
                const type = module ? "add" : grip ? "move" : resize ? "resize" : "canvas";
                drag = { type, canvas, capture, corner: canvasResize?.dataset.canvasResize || "se", overlayId: stage.dataset.overlayId, pointerId: e.pointerId, startX: e.clientX, startY: e.clientY, moved: false, scale: canvas.rect.width / (canvas.width + canvas.chromeX) };
                drag.wrapRect = wrap.getBoundingClientRect();
                if (module) {
                    drag.kind = module.dataset.moduleKind;
                    drag.label = module.querySelector("strong")?.textContent || "Modul";
                    drag.width = number(module, "moduleWidth"); drag.height = number(module, "moduleHeight");
                } else {
                    e.preventDefault();
                    const widget = capture.closest(".oe-widget");
                    if (widget) {
                        drag.element = widget; drag.id = widget.dataset.widgetId;
                        drag.x = number(widget, "x"); drag.y = number(widget, "y");
                        drag.width = number(widget, "width"); drag.height = number(widget, "height");
                        widget.focus({ preventScroll: true });
                        invoke("SelectWidget", drag.id);
                    } else if (type === "canvas") {
                        capture.focus({ preventScroll: true });
                    }
                }
                drag.peers = Array.from(stage.querySelectorAll(".oe-widget"))
                    .filter(element => element !== drag.element)
                    .map(element => ({ id: element.dataset.widgetId, x: number(element, "x"), y: number(element, "y"),
                        width: number(element, "width"), height: number(element, "height") }));
                capture.setPointerCapture(e.pointerId);
            };
            const move = e => {
                if (!drag || drag.pointerId !== e.pointerId) return;
                const dx = (e.clientX - drag.startX) / drag.scale, dy = (e.clientY - drag.startY) / drag.scale;
                if (!drag.moved && Math.abs(e.clientX - drag.startX) + Math.abs(e.clientY - drag.startY) < 5) return;
                drag.moved = true; e.preventDefault();
                document.body.classList.add(drag.type === "resize" || drag.type === "canvas" ? "oe-resizing" : "oe-dragging");
                if (drag.type === "add") {
                    if (!ghost) {
                        ghost = document.createElement("div"); ghost.className = "overlay-editor oe-drag-ghost";
                        ghost.textContent = drag.label; document.body.appendChild(ghost);
                    }
                    ghost.style.left = `${e.clientX + 12}px`; ghost.style.top = `${e.clientY + 12}px`;
                    const rect = contentRect();
                    const inside = e.clientX >= rect.left && e.clientX <= rect.right && e.clientY >= rect.top && e.clientY <= rect.bottom;
                    stage.classList.toggle("is-drop-target", inside);
                    if (inside) align({ x: (e.clientX - rect.left) / drag.scale, y: (e.clientY - rect.top) / drag.scale,
                        width: drag.width, height: drag.height }, "move", e);
                    else showGuides([]);
                    return;
                }
                if (drag.type === "canvas") {
                    const west = drag.corner.includes("w"), north = drag.corner.includes("n");
                    const widgets = Array.from(stage.querySelectorAll(".oe-widget"));
                    const minWidth = west && widgets.length ? Math.max(160, drag.canvas.width - Math.min(...widgets.map(element => number(element, "x")))) : 160;
                    const minHeight = north && widgets.length ? Math.max(64, drag.canvas.height - Math.min(...widgets.map(element => number(element, "y")))) : 64;
                    const maxWidth = west && widgets.length ? Math.min(1600, drag.canvas.width + 1600 - Math.max(...widgets.map(element => number(element, "x") + number(element, "width")))) : 1600;
                    const maxHeight = north && widgets.length ? Math.min(1200, drag.canvas.height + 1200 - Math.max(...widgets.map(element => number(element, "y") + number(element, "height")))) : 1200;
                    document.body.classList.toggle("oe-resizing-reverse", west !== north);
                    drag.newWidth = clamp(grid(drag.canvas.width + (west ? -dx : dx)), minWidth, maxWidth);
                    drag.newHeight = clamp(grid(drag.canvas.height + (north ? -dy : dy)), minHeight, maxHeight);
                    wrap.style.position = "relative";
                    wrap.style.left = "0px"; wrap.style.top = "0px";
                    const outerWidth = drag.newWidth + drag.canvas.chromeX, outerHeight = drag.newHeight + drag.canvas.chromeY;
                    stage.style.width = `${outerWidth}px`; stage.style.height = `${outerHeight}px`;
                    wrap.style.width = `${outerWidth * drag.scale}px`; wrap.style.height = `${outerHeight * drag.scale}px`;
                    const layoutRect = wrap.getBoundingClientRect();
                    wrap.style.left = `${drag.wrapRect.left-layoutRect.left+(west ? (drag.canvas.width-drag.newWidth)*drag.scale : 0)}px`;
                    wrap.style.top = `${drag.wrapRect.top-layoutRect.top+(north ? (drag.canvas.height-drag.newHeight)*drag.scale : 0)}px`;
                    widgets.forEach(element => {
                        element.style.left = `${number(element,"x")+(west ? drag.newWidth-drag.canvas.width : 0)}px`;
                        element.style.top = `${number(element,"y")+(north ? drag.newHeight-drag.canvas.height : 0)}px`;
                    });
                    return;
                }
                if (drag.type === "move") {
                    const aligned = align({ x: drag.x + dx, y: drag.y + dy, width: drag.width, height: drag.height }, "move", e);
                    drag.newX = aligned.x;
                    drag.newY = aligned.y;
                    drag.element.style.left = `${drag.newX}px`; drag.element.style.top = `${drag.newY}px`;
                } else {
                    const aligned = align({ x: drag.x, y: drag.y, width: drag.width + dx, height: drag.height + dy }, "resize", e);
                    drag.newWidth = aligned.width;
                    drag.newHeight = aligned.height;
                    drag.element.style.width = `${drag.newWidth}px`; drag.element.style.height = `${drag.newHeight}px`;
                    resizeContent(drag.element, drag.newWidth, drag.newHeight);
                }
            };
            const up = e => {
                if (!drag || drag.pointerId !== e.pointerId) return;
                const current = drag;
                const cancelled = e.type === "pointercancel";
                if (current.moved) {
                    suppressClick = true;
                    // Cancel only the synthetic click immediately following this
                    // drag; a later deliberate click on a module remains usable.
                    setTimeout(() => suppressClick = false, 0);
                    if (cancelled) restore(current);
                    else if (current.type === "add") {
                        const rect = contentRect();
                        if (e.clientX >= rect.left && e.clientX <= rect.right && e.clientY >= rect.top && e.clientY <= rect.bottom) {
                            const aligned = align({ x: (e.clientX - rect.left) / current.scale, y: (e.clientY - rect.top) / current.scale,
                                width: current.width, height: current.height }, "move", e);
                            invoke("AddModuleAt", current.kind, aligned.x, aligned.y);
                        }
                    } else if (current.type === "canvas") invoke("CommitCanvasCorner", current.newWidth, current.newHeight, current.corner);
                    else invoke("CommitWidgetGeometry", current.id, current.newX ?? current.x, current.newY ?? current.y, current.newWidth ?? current.width, current.newHeight ?? current.height);
                }
                cleanupDrag();
                if (cancelled || !current.moved) fit();
            };
            const click = e => { if (suppressClick) { e.preventDefault(); e.stopImmediatePropagation(); suppressClick = false; } };
            const outsideClick = e => {
                if (drag || suppressClick || !stage.querySelector(".oe-widget.is-selected")) return;
                // Keep the selection while editing its properties or adding a module.
                // Clicks inside the canvas already use the Blazor selection handlers.
                if (stage.contains(e.target) || inspector?.contains(e.target)
                    || (root.contains(e.target) && e.target.closest("[data-module-kind]"))) return;
                // User request 2026-11-09: browsing modules must keep the current widget selection.
                if (root.contains(e.target) && e.target.closest(".oe-library-controls")) return;
                invoke("ClearSelection");
            };
            const key = e => {
                if (e.key === "Escape" && drag) {
                    restore(drag); cleanupDrag(); fit(); e.preventDefault(); return;
                }
                if (e.altKey || e.ctrlKey || e.metaKey || e.target.matches("input,select,textarea") || e.target.closest("[data-widget-delete]")) return;
                const widget = e.target.closest(".oe-widget");
                if (!widget) return;
                if (e.target === widget && (e.key === "Enter" || e.key === " ")) {
                    e.preventDefault();
                    invoke("SelectWidget", widget.dataset.widgetId);
                    return;
                }
                const step = e.shiftKey ? 8 : 1;
                const deltas = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
                const delta = deltas[e.key];
                if (delta) { e.preventDefault(); invoke("NudgeWidget", widget.dataset.widgetId, ...delta); }
            };
            const wheel = e => {
                if (e.ctrlKey) return;
                // Focused native controls otherwise consume the wheel and can
                // silently resize the overlay. Leave scrolling to the browser.
                const field = e.target.closest("input[type=number], input[type=range], select");
                if (field && field === document.activeElement) field.blur();
            };
            if (!readOnly) {
                root.addEventListener("pointerdown", down);
                root.addEventListener("pointermove", move);
                root.addEventListener("pointerup", up);
                root.addEventListener("pointercancel", up);
                root.addEventListener("click", click, true);
                root.addEventListener("keydown", key);
                root.addEventListener("wheel", wheel, { capture: true, passive: true });
                document.addEventListener("click", outsideClick);
            }
            const resizeObserver = new ResizeObserver(fit); resizeObserver.observe(viewport);
            const mutationObserver = new MutationObserver(records => {
                // Drawing pointer guides must not trigger a text/layout fit for every widget.
                if (records.length && records.every(record => record.target === guideLayer || guideLayer?.contains(record.target))) return;
                fit();
            });
            mutationObserver.observe(stage, { attributes: true, subtree: true, childList: true, characterData: true,
                attributeFilter: ["data-overlay-id", "data-width", "data-height", "data-chrome-x", "data-chrome-y", "data-content-width", "data-content-height", "data-min-content-width", "data-min-content-height", "data-content-layout"] });
            fit();
            editors.set(id, () => {
                disposed = true;
                if (contentFrame !== null) cancelAnimationFrame(contentFrame);
                cleanupDrag(); resizeObserver.disconnect(); mutationObserver.disconnect();
                root.removeEventListener("pointerdown", down); root.removeEventListener("pointermove", move);
                root.removeEventListener("pointerup", up); root.removeEventListener("pointercancel", up);
                root.removeEventListener("click", click, true); root.removeEventListener("keydown", key);
                root.removeEventListener("wheel", wheel, true);
                document.removeEventListener("click", outsideClick);
            });
        },
        unmount(id) { editors.get(id)?.(); editors.delete(id); }
    };
})();
