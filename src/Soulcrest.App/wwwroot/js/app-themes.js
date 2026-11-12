// User request 2026-11-11: only the main document receives an app theme.
window.soulcrestAppTheme = {
    apply(id, variables) {
        if (id !== 'soulcrest-classic' && id !== 'graphite-dark') return null;
        const root = document.documentElement;
        if (!root || typeof variables !== 'string') return null;
        // CSS values come from AppThemeDefinition, not from settings text or remote content.
        const entries = variables.split(';').filter(Boolean).map(entry => entry.split(':'));
        const names = ['--bg', '--panel', '--panel-2', '--line', '--text', '--muted', '--accent', '--accent-ink'];
        if (entries.length !== names.length || entries.some(([name, value, extra]) =>
            extra !== undefined || !names.includes(name) || !/^#[0-9a-f]{6}$/i.test(value)) ||
            new Set(entries.map(([name]) => name)).size !== names.length) return null;
        for (const [name, value] of entries) root.style.setProperty(name, value);
        root.dataset.appTheme = id;
        const applied = window.getComputedStyle(root);
        return entries.every(([name, value]) => applied.getPropertyValue(name).trim().toLowerCase() === value.toLowerCase())
            ? id : null;
    }
};
