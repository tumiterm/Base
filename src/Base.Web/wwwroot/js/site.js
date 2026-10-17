// Sends the antiforgery token on every same-origin fetch that changes data.
// The server requires it on POST/PUT/PATCH/DELETE because the API shares the auth cookie with the pages.
(() => {
    const token = document.querySelector('meta[name="csrf-token"]')?.content;
    if (!token) return;
    const originalFetch = window.fetch;
    window.fetch = (input, init = {}) => {
        const method = (init.method || 'GET').toUpperCase();
        if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
            init.headers = new Headers(init.headers);
            init.headers.set('X-CSRF-TOKEN', token);
        }
        return originalFetch(input, init);
    };
})();

// Forms marked .confirm-submit ask before submitting (data-confirm holds the question).
document.addEventListener('submit', (event) => {
    const form = event.target.closest('form.confirm-submit');
    if (form && !window.confirm(form.dataset.confirm || 'Are you sure?')) {
        event.preventDefault();
    }
});

// A .drop-zone wraps a file input: dropping a file on it fills the input. The input still works on its own.
document.querySelectorAll('.drop-zone').forEach((zone) => {
    const input = zone.querySelector('input[type="file"]');
    if (!input) return;
    ['dragenter', 'dragover'].forEach((name) => zone.addEventListener(name, (event) => {
        event.preventDefault();
        zone.classList.add('drop-zone-active');
    }));
    ['dragleave', 'drop'].forEach((name) => zone.addEventListener(name, () => zone.classList.remove('drop-zone-active')));
    zone.addEventListener('drop', (event) => {
        event.preventDefault();
        if (event.dataTransfer?.files.length) {
            input.files = event.dataTransfer.files;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }
    });
});

// ---------------------------------------------------------------------------
// App shell: navigation drawer (phones), collapsible sidebar (desktop), theme,
// command palette (Ctrl/Cmd+K), toasts and phone-friendly table labels.
// ---------------------------------------------------------------------------
(() => {
    const app = document.getElementById('app');
    const store = {
        get: (key) => { try { return localStorage.getItem(key); } catch { return null; } },
        set: (key, value) => { try { value === null ? localStorage.removeItem(key) : localStorage.setItem(key, value); } catch { /* ignore */ } },
    };

    // Sidebar: drawer on small screens, icon rail when collapsed on large ones.
    if (app) {
        if (document.documentElement.classList.contains('nav-collapsed')) app.classList.add('is-collapsed');
        const closeNav = () => app.classList.remove('is-nav-open');
        document.querySelectorAll('[data-nav-open]').forEach((b) => b.addEventListener('click', () => {
            app.classList.add('is-nav-open');
            app.querySelector('.sidebar-nav a')?.focus();
        }));
        document.querySelectorAll('[data-nav-close]').forEach((b) => b.addEventListener('click', closeNav));
        document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeNav(); });
        document.querySelectorAll('[data-nav-collapse]').forEach((b) => b.addEventListener('click', () => {
            const collapsed = app.classList.toggle('is-collapsed');
            document.documentElement.classList.toggle('nav-collapsed', collapsed);
            store.set('nav-collapsed', collapsed ? '1' : null);
        }));
    }

    // Theme: Auto (device) / Light / Dark, remembered in this browser.
    const root = document.documentElement;
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const choice = () => store.get('theme') || root.dataset.defaultTheme || 'system';
    const apply = (value) => {
        const dark = value === 'dark' || (value === 'system' && media.matches);
        root.setAttribute('data-bs-theme', dark ? 'dark' : 'light');
        document.querySelectorAll('[data-theme-choice]').forEach((r) => { r.checked = r.value === value; });
    };
    apply(choice());
    media.addEventListener('change', () => apply(choice()));
    document.querySelectorAll('[data-theme-choice]').forEach((r) => r.addEventListener('change', () => { store.set('theme', r.value); apply(r.value); }));
    document.querySelectorAll('[data-theme-toggle]').forEach((b) => b.addEventListener('click', () => {
        const next = root.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
        store.set('theme', next);
        apply(next);
    }));

    // Toasts: success fades after a few seconds; errors stay until dismissed.
    const dismiss = (toast) => { toast.classList.add('is-leaving'); toast.addEventListener('animationend', () => toast.remove(), { once: true }); };
    document.querySelectorAll('.toast-item').forEach((toast) => {
        toast.querySelector('[data-toast-close]')?.addEventListener('click', () => dismiss(toast));
        if (toast.hasAttribute('data-autohide')) setTimeout(() => dismiss(toast), 5000);
    });

    // Tables: copy header text onto cells so the phone layout can label each value.
    document.querySelectorAll('.app-content table.table').forEach((table) => {
        const headers = [...table.querySelectorAll('thead th')].map((th) => th.textContent.trim());
        if (headers.length === 0) return;
        table.querySelectorAll('tbody tr').forEach((row) => {
            [...row.children].forEach((cell, i) => { if (!cell.hasAttribute('data-label')) cell.setAttribute('data-label', headers[i] ?? ''); });
        });
    });

    // Command palette: every navigation link (and any [data-palette-label] action), filtered as you type.
    const palette = document.getElementById('palette');
    if (palette) {
        const input = document.getElementById('palette-input');
        const list = document.getElementById('palette-list');
        const entries = [...document.querySelectorAll('.sidebar-nav a.nav-link-app, [data-palette-label]')]
            .map((a) => ({
                label: a.dataset.paletteLabel || a.textContent.trim(),
                section: a.closest('.nav-section')?.querySelector('.nav-section-label')?.textContent.trim() ?? 'Action',
                href: a.getAttribute('href'),
                icon: a.querySelector('use')?.getAttribute('href'),
            }))
            .filter((e, i, all) => e.href && all.findIndex((x) => x.href === e.href) === i);
        let selected = 0;
        let shown = entries;
        const render = () => {
            list.replaceChildren(...shown.map((entry, i) => {
                const li = document.createElement('li');
                li.className = 'palette-item';
                li.setAttribute('role', 'option');
                li.setAttribute('aria-selected', i === selected ? 'true' : 'false');
                li.id = `palette-${i}`;
                if (entry.icon) {
                    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                    svg.setAttribute('class', 'icon');
                    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
                    use.setAttribute('href', entry.icon);
                    svg.append(use);
                    li.append(svg);
                }
                li.append(document.createTextNode(entry.label));
                const small = document.createElement('small');
                small.textContent = entry.section;
                li.append(small);
                li.addEventListener('mousemove', () => { if (selected !== i) { selected = i; render(); } });
                li.addEventListener('click', () => { window.location.href = entry.href; });
                return li;
            }));
            if (shown.length === 0) {
                const empty = document.createElement('li');
                empty.className = 'palette-empty';
                empty.textContent = 'Nothing matches.';
                list.append(empty);
            }
            input.setAttribute('aria-activedescendant', shown.length ? `palette-${selected}` : '');
            list.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: 'nearest' });
        };
        const open = () => { palette.classList.add('is-open'); input.value = ''; shown = entries; selected = 0; render(); input.focus(); };
        const close = () => palette.classList.remove('is-open');
        input.addEventListener('input', () => {
            const q = input.value.trim().toLowerCase();
            shown = entries.filter((e) => `${e.label} ${e.section}`.toLowerCase().includes(q));
            selected = 0;
            render();
        });
        input.addEventListener('keydown', (e) => {
            if (e.key === 'ArrowDown') { e.preventDefault(); selected = Math.min(selected + 1, shown.length - 1); render(); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); selected = Math.max(selected - 1, 0); render(); }
            else if (e.key === 'Enter' && shown[selected]) { window.location.href = shown[selected].href; }
            else if (e.key === 'Escape') { close(); }
        });
        palette.addEventListener('click', (e) => { if (e.target === palette) close(); });
        document.querySelectorAll('[data-palette-open]').forEach((b) => b.addEventListener('click', open));
        document.addEventListener('keydown', (e) => {
            if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); palette.classList.contains('is-open') ? close() : open(); }
        });
    }
})();

// Appearance page: preview the accent live (inline styles are blocked by the CSP, so set CSS variables from script).
(() => {
    const form = document.getElementById('appearance-form');
    if (!form) return;
    document.querySelectorAll('[data-swatch]').forEach((s) => s.style.setProperty('background', s.dataset.swatch));
    const text = document.getElementById('primaryColor');
    const picker = document.getElementById('accent-picker');
    const preview = document.getElementById('accent-preview');
    const hex = (v) => /^#[0-9a-f]{6}$/i.test(v);
    const mix = (a, b, t) => '#' + [0, 2, 4].map((i) => Math.round(parseInt(a.substr(1 + i, 2), 16) * (1 - t) + parseInt(b.substr(1 + i, 2), 16) * t).toString(16).padStart(2, '0')).join('');
    const lum = (c) => [0, 2, 4].map((i) => parseInt(c.substr(1 + i, 2), 16) / 255).map((v) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4)).reduce((s, v, i) => s + v * [0.2126, 0.7152, 0.0722][i], 0);
    const contrast = (a, b) => { const [x, y] = [lum(a), lum(b)]; return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
    const show = (value) => {
        if (!hex(value)) return;
        const dark = document.documentElement.getAttribute('data-bs-theme') === 'dark';
        const accent = dark ? mix(value, '#ffffff', 0.22) : value;
        preview.style.setProperty('--accent', accent);
        preview.style.setProperty('--accent-hover', mix(accent, '#000000', 0.1));
        preview.style.setProperty('--accent-subtle', dark ? `${accent}29` : mix(accent, '#ffffff', 0.9));
        preview.style.setProperty('--accent-subtle-text', dark ? mix(accent, '#ffffff', 0.35) : mix(accent, '#000000', 0.28));
        preview.style.setProperty('--accent-contrast', contrast(accent, '#ffffff') >= contrast(accent, '#0b0d12') ? '#ffffff' : '#0b0d12');
    };
    const set = (value, from) => {
        if (from !== text) text.value = value;
        if (from !== picker && hex(value)) picker.value = value;
        document.querySelectorAll('[data-accent-preset]').forEach((r) => { r.checked = r.value.toLowerCase() === value.toLowerCase(); });
        show(value);
    };
    document.querySelectorAll('[data-accent-preset]').forEach((r) => r.addEventListener('change', () => set(r.value, r)));
    picker.addEventListener('input', () => set(picker.value, picker));
    text.addEventListener('input', () => set(text.value.trim(), text));
})();

// Small form helpers: usage meters, number steppers, a code derived from a name, and "unlimited" switches.
(() => {
    document.querySelectorAll('[data-meter]').forEach((m) => {
        const fill = m.querySelector('.meter-fill');
        if (fill) fill.style.setProperty('--meter', `${Math.max(0, Math.min(100, Number(m.dataset.meter) || 0))}%`);
    });

    document.querySelectorAll('[data-stepper]').forEach((s) => {
        const input = s.querySelector('input');
        s.querySelectorAll('[data-step]').forEach((b) => b.addEventListener('click', () => {
            const min = input.min === '' ? -Infinity : Number(input.min);
            const max = input.max === '' ? Infinity : Number(input.max);
            input.value = String(Math.max(min, Math.min(max, (Number(input.value) || 0) + Number(b.dataset.step))));
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }));
    });

    // Fills a code field from a name until the user edits the code themselves.
    document.querySelectorAll('[data-slug-from]').forEach((code) => {
        const source = document.querySelector(code.dataset.slugFrom);
        if (!source) return;
        let touched = code.value !== '';
        code.addEventListener('input', () => { touched = code.value !== ''; });
        source.addEventListener('input', () => {
            if (touched) return;
            code.value = source.value.normalize('NFKD').replace(/[̀-ͯ]/g, '').toLowerCase()
                .replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 50);
        });
    });

    document.querySelectorAll('[data-unlimited-for]').forEach((toggle) => {
        const input = document.querySelector(toggle.dataset.unlimitedFor);
        if (!input) return;
        const sync = () => { input.disabled = toggle.checked; if (toggle.checked) input.value = ''; };
        toggle.addEventListener('change', sync);
        sync();
    });
})();
