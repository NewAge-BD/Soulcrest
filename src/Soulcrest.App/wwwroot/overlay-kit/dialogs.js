/* Grindcrest overlay-editor-kit 1.15.0; source 231a2f0d40dcba872a2a83aa86b8f7b7a1edf49d. */
// The source dialog helpers only; no tracker or application-wide helpers.
window.soulcrestOverlayDialogs = {
    showDialog: function (id) {
        const dialog = document.getElementById(id);
        if (dialog && !dialog.open) {
            if (dialog.hasAttribute('data-managed-cancel') && !dialog.dataset.managedCancelBound) {
                dialog.dataset.managedCancelBound = 'true';
                dialog.addEventListener('cancel', event => {
                    event.preventDefault();
                    dialog.querySelector('[data-dialog-close]')?.click();
                });
            }
            if (dialog.hasAttribute('data-backdrop-close') && !dialog.dataset.backdropCloseBound) {
                dialog.dataset.backdropCloseBound = 'true';
                dialog.addEventListener('click', event => {
                    if (event.target !== dialog) return;
                    const bounds = dialog.getBoundingClientRect();
                    if (event.clientX >= bounds.left && event.clientX <= bounds.right &&
                        event.clientY >= bounds.top && event.clientY <= bounds.bottom) return;
                    dialog.querySelector('[data-dialog-close]')?.click();
                });
            }
            dialog.showModal();
            const focus = dialog.querySelector('[autofocus]') || dialog.querySelector('button, input, select');
            if (focus) focus.focus();
        }
    },
    closeDialog: function (id) { const dialog = document.getElementById(id); if (dialog && dialog.open) dialog.close(); }
};
