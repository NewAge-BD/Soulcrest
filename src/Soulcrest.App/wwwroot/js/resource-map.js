window.soulcrestResourceMap = {
    dialog(mixedIds) {
        const dialog = document.querySelector('.resource-map-dialog');
        if (!dialog) return;
        const mixed = new Set(mixedIds || []);
        for (const input of dialog.querySelectorAll('.resource-map-options input')) input.indeterminate = mixed.has(input.id);
        if (!dialog.contains(document.activeElement)) dialog.querySelector('input')?.focus();
        if (dialog.dataset.trapBound) return;
        dialog.dataset.trapBound = 'true';
        dialog.addEventListener('keydown', e => {
            if (e.key !== 'Tab') return;
            const fields = [...dialog.querySelectorAll('button:not(:disabled),input:not(:disabled)')];
            const first = fields[0], last = fields[fields.length - 1];
            if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last?.focus(); }
            else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first?.focus(); }
        });
    }
};
