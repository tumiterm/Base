// Runs in <head>, before first paint, so the page never flashes the wrong theme.
// Choice order: the user's pick (this browser) → the tenant default → the device setting.
(() => {
    let choice = null;
    try { choice = localStorage.getItem('theme'); } catch { /* storage blocked: fall through */ }
    const fallback = document.documentElement.dataset.defaultTheme || 'system';
    const wanted = choice || fallback;
    const dark = wanted === 'dark' || (wanted === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.setAttribute('data-bs-theme', dark ? 'dark' : 'light');
    try {
        if (localStorage.getItem('nav-collapsed') === '1') document.documentElement.classList.add('nav-collapsed');
    } catch { /* ignore */ }
})();
