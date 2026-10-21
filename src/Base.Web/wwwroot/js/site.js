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

// Menus position themselves against the viewport, so a row's menu isn't clipped by the table, panel or
// scrolling container it sits in (tables use overflow: clip for their rounded corners).
if (window.bootstrap?.Dropdown) {
    bootstrap.Dropdown.Default.popperConfig = (config) => ({ ...config, strategy: 'fixed' });
}

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

    // Sidebar groups: open or closed as the person left them; the group holding the current page is always open.
    document.querySelectorAll('[data-nav-group]').forEach((group) => {
        const key = `nav-group:${group.dataset.navGroup}`;
        const toggle = group.querySelector('.nav-group-toggle');
        const set = (open) => { group.classList.toggle('is-open', open); toggle.setAttribute('aria-expanded', open ? 'true' : 'false'); };
        if (!group.classList.contains('is-current') && store.get(key) === '1') set(true);
        toggle.addEventListener('click', () => {
            const open = !group.classList.contains('is-open');
            set(open);
            store.set(key, open ? '1' : null);
        });
    });

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
                section: (a.closest('.nav-group')?.querySelector('.nav-group-toggle') ?? a.closest('.nav-section')?.querySelector('.nav-section-label'))?.textContent.trim() ?? 'Action',
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

// Searchable combobox built from a <select data-combobox>. The select stays in the form (hidden) and holds the value,
// so the page works without script; this adds typing to filter, arrow keys, Enter to pick and Escape to close.
(() => {
    let counter = 0;
    document.querySelectorAll('select[data-combobox]').forEach((select) => {
        const id = `cb-${++counter}`;
        const wrap = document.createElement('div');
        wrap.className = 'combobox';
        const input = document.createElement('input');
        input.type = 'text';
        input.className = 'form-control combobox-input';
        input.id = select.id ? `${select.id}-search` : `${id}-input`;
        input.setAttribute('role', 'combobox');
        input.setAttribute('aria-autocomplete', 'list');
        input.setAttribute('aria-expanded', 'false');
        input.setAttribute('aria-controls', `${id}-list`);
        input.autocomplete = 'off';
        input.placeholder = select.dataset.placeholder || 'Search…';
        if (select.disabled) input.disabled = true;
        const list = document.createElement('ul');
        list.className = 'combobox-list';
        list.id = `${id}-list`;
        list.setAttribute('role', 'listbox');
        list.hidden = true;
        const chevron = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        chevron.setAttribute('class', 'icon combobox-chevron');
        chevron.setAttribute('aria-hidden', 'true');
        const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
        use.setAttribute('href', '/img/icons.svg#chevron-down');
        chevron.appendChild(use);

        select.hidden = true;
        select.tabIndex = -1;
        select.parentNode.insertBefore(wrap, select);
        wrap.append(input, chevron, list, select);
        const label = select.id ? document.querySelector(`label[for="${select.id}"]`) : null;
        if (label) label.htmlFor = input.id;

        const options = () => [...select.options];
        const textOf = (o) => (o && o.value !== '' ? o.text : '');
        let active = -1;
        let shown = [];
        const sync = () => { input.value = textOf(select.selectedOptions[0]); };
        const render = (filter) => {
            const term = (filter || '').trim().toLowerCase();
            shown = options().filter((o) => !term || o.text.toLowerCase().includes(term));
            list.replaceChildren(...shown.map((o, i) => {
                const li = document.createElement('li');
                li.className = `combobox-option${o.value === '' ? ' is-empty' : ''}`;
                li.id = `${id}-o${i}`;
                li.setAttribute('role', 'option');
                li.setAttribute('aria-selected', String(o.selected && o.value !== ''));
                li.textContent = o.text;
                li.addEventListener('mousedown', (e) => { e.preventDefault(); choose(o); });
                return li;
            }));
            if (shown.length === 0) {
                const none = document.createElement('li');
                none.className = 'combobox-none';
                none.textContent = 'No matches';
                list.appendChild(none);
            }
            highlight(shown.findIndex((o) => o.selected));
        };
        const highlight = (index) => {
            active = index;
            [...list.children].forEach((li, i) => li.classList.toggle('is-active', i === index));
            const current = list.children[index];
            if (current && current.id) { input.setAttribute('aria-activedescendant', current.id); current.scrollIntoView({ block: 'nearest' }); } else input.removeAttribute('aria-activedescendant');
        };
        const open = () => { if (input.disabled || input.readOnly) return; render(''); list.hidden = false; input.setAttribute('aria-expanded', 'true'); };
        const close = () => { list.hidden = true; input.setAttribute('aria-expanded', 'false'); input.removeAttribute('aria-activedescendant'); };
        const choose = (option) => {
            select.value = option.value;
            select.dispatchEvent(new Event('change', { bubbles: true }));
            sync();
            close();
        };

        input.addEventListener('focus', () => { open(); input.select(); });
        input.addEventListener('input', () => { if (list.hidden) open(); render(input.value); highlight(shown.length ? 0 : -1); });
        input.addEventListener('blur', () => { close(); sync(); });
        input.addEventListener('keydown', (e) => {
            if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
                e.preventDefault();
                if (list.hidden) { open(); return; }
                const step = e.key === 'ArrowDown' ? 1 : -1;
                highlight(Math.max(0, Math.min(shown.length - 1, active + step)));
            } else if (e.key === 'Enter' && !list.hidden) {
                e.preventDefault();
                if (shown[active]) choose(shown[active]);
            } else if (e.key === 'Escape' && !list.hidden) {
                e.preventDefault();
                e.stopPropagation();
                close();
                sync();
            }
        });
        select.addEventListener('change', sync);
        new MutationObserver(() => { input.disabled = select.disabled; }).observe(select, { attributes: true, attributeFilter: ['disabled'] });
        sync();
    });
})();

// Forms that read as text until "Edit" is pressed ([data-edit-form]); inline row inputs; filters that apply on change.
(() => {
    document.querySelectorAll('[data-edit-form]').forEach((form) => {
        const fields = form.querySelector('fieldset');
        const blanks = () => form.querySelectorAll('fieldset input.form-control:not(.combobox-input), fieldset textarea.form-control');
        const setEditing = (editing) => {
            form.classList.toggle('form-reading', !editing);
            form.classList.toggle('form-editing', editing);
            // Reading: an empty field shows a dash, and an empty date doesn't show the browser's mm/dd/yyyy.
            blanks().forEach((field) => {
                if (!editing) {
                    if (field.dataset.placeholder === undefined) field.dataset.placeholder = field.placeholder || '';
                    field.placeholder = '—';
                    if (field.type === 'date') {
                        field.dataset.type = 'date';
                        field.dataset.value = field.value;
                        const [y, m, d] = field.value.split('-').map(Number);
                        field.type = 'text';
                        field.value = y ? new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'long', year: 'numeric' }).format(new Date(y, m - 1, d)) : '';
                    }
                } else {
                    if (field.dataset.type) { field.type = field.dataset.type; field.value = field.dataset.value ?? ''; delete field.dataset.type; delete field.dataset.value; }
                    if (field.dataset.placeholder !== undefined) field.placeholder = field.dataset.placeholder;
                }
            });
            if (fields) fields.disabled = !editing;
            form.querySelectorAll('.combobox-input').forEach((i) => { i.disabled = !editing; });
        };
        form.querySelectorAll('[data-edit-start]').forEach((b) => b.addEventListener('click', () => {
            setEditing(true);
            const first = form.querySelector('fieldset input:not([type=hidden]), fieldset textarea');
            if (first) first.focus();
        }));
        form.querySelectorAll('[data-edit-cancel]').forEach((b) => b.addEventListener('click', () => { form.reset(); form.querySelectorAll('select').forEach((s) => s.dispatchEvent(new Event('change'))); setEditing(false); }));
        form.addEventListener('keydown', (e) => { if (e.key === 'Escape' && form.classList.contains('form-editing')) form.querySelector('[data-edit-cancel]')?.click(); });
        setEditing(form.dataset.editForm === 'editing');
    });

    document.querySelectorAll('.inline-input').forEach((input) => {
        const original = input.value;
        input.addEventListener('keydown', (e) => { if (e.key === 'Escape') { input.value = original; input.blur(); } });
    });

    document.querySelectorAll('[data-autosubmit]').forEach((control) => control.addEventListener('change', () => control.form?.requestSubmit()));
})();

// Copy-to-clipboard: <button data-copy="#element-id">, with a "Copied" confirmation on the button.
(() => {
    document.querySelectorAll('[data-copy]').forEach((button) => button.addEventListener('click', async () => {
        const source = document.querySelector(button.dataset.copy);
        if (!source || !navigator.clipboard) return;
        await navigator.clipboard.writeText(source.textContent.trim());
        const label = button.querySelector('[data-copy-label]');
        if (label) {
            const original = label.textContent;
            label.textContent = 'Copied';
            setTimeout(() => { label.textContent = original; }, 2000);
        }
    }));
})();

// Invitation links: <form data-invite-link> posts for a fresh link and puts it on the clipboard. The clipboard write
// starts inside the click (ClipboardItem with a promise) so Safari allows it; if it's blocked, the form is submitted
// normally and the server shows the link on a page instead.
(() => {
    const sprite = document.querySelector('svg use')?.getAttribute('href')?.split('#')[0] ?? '';
    const icon = (name) => {
        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('class', 'icon');
        svg.setAttribute('aria-hidden', 'true');
        const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
        use.setAttribute('href', `${sprite}#${name}`);
        svg.append(use);
        return svg;
    };
    const toast = (text, ok) => {
        let stack = document.querySelector('.toast-stack');
        if (!stack) { stack = document.createElement('div'); stack.className = 'toast-stack'; stack.setAttribute('aria-live', 'polite'); document.body.append(stack); }
        const item = document.createElement('div');
        item.className = `toast-item ${ok ? 'toast-success' : 'toast-error'}`;
        item.setAttribute('role', ok ? 'status' : 'alert');
        const body = document.createElement('div');
        body.className = 'toast-body';
        body.textContent = text;
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'icon-btn toast-close';
        close.setAttribute('aria-label', 'Dismiss');
        close.append(icon('x'));
        const leave = () => { item.classList.add('is-leaving'); item.addEventListener('animationend', () => item.remove(), { once: true }); };
        close.addEventListener('click', leave);
        item.append(icon(ok ? 'circle-check' : 'circle-alert'), body, close);
        stack.append(item);
        if (ok) setTimeout(leave, 6000);
    };
    const hoursLeft = (iso) => Math.max(1, Math.round((new Date(iso) - Date.now()) / 3600000));

    document.querySelectorAll('form[data-invite-link]').forEach((form) => form.addEventListener('submit', async (e) => {
        if (!navigator.clipboard) return; // Not a secure context: let the page show the link.
        e.preventDefault();
        const request = fetch(form.action, { method: 'POST', body: new FormData(form), headers: { Accept: 'application/json' }, credentials: 'same-origin' })
            .then(async (response) => {
                const body = await response.json().catch(() => ({}));
                if (!response.ok) throw Object.assign(new Error(body.detail || 'The link could not be made.'), { server: true });
                return body;
            });
        try {
            if (window.ClipboardItem && navigator.clipboard.write) {
                await navigator.clipboard.write([new ClipboardItem({ 'text/plain': request.then((link) => new Blob([link.url], { type: 'text/plain' })) })]);
            } else {
                await navigator.clipboard.writeText((await request).url);
            }
            const link = await request;
            toast(`Invitation link for ${link.displayName} copied. It works once, for the next ${hoursLeft(link.expiresAt)} hours.`, true);
        } catch (error) {
            if (error.server) toast(error.message, false);
            else form.submit();
        }
    }));
})();

// Plans and features grid: "Include in / Leave out of every plan" per module, and a warning before leaving with
// unsaved changes.
(() => {
    document.querySelectorAll('form[data-plan-matrix]').forEach((form) => {
        const footer = form.querySelector('.matrix-footer');
        let dirty = false;
        const markDirty = () => { dirty = true; footer?.classList.add('is-dirty'); };
        form.addEventListener('change', markDirty);
        form.addEventListener('input', markDirty);
        form.addEventListener('submit', () => { dirty = false; });
        window.addEventListener('beforeunload', (e) => { if (dirty) { e.preventDefault(); e.returnValue = ''; } });
        form.querySelectorAll('[data-matrix-set]').forEach((button) => button.addEventListener('click', () => {
            const on = button.dataset.matrixSet === 'on';
            button.closest('[data-matrix-module]')?.querySelectorAll('[data-matrix-feature]').forEach((box) => { box.checked = on; });
            markDirty();
        }));
    });
})();

// Message composer and templates: a small formatting editor, placeholders, the people picker, live reach, channel
// sections and the SMS counter. Everything also works as a plain form post; this only makes it quicker.
(() => {
    // Formatting editor: a contenteditable area mirrored into a hidden textarea (cleaned again on the server).
    document.querySelectorAll('[data-rich-editor]').forEach((editor) => {
        const content = editor.querySelector('[data-rich-content]');
        const value = editor.querySelector('[data-rich-value]');
        const sync = () => { value.value = content.innerHTML.trim() === '<br>' ? '' : content.innerHTML; };
        content.addEventListener('input', sync);
        content.addEventListener('paste', (e) => {
            e.preventDefault();
            document.execCommand('insertText', false, e.clipboardData?.getData('text/plain') ?? '');
        });
        editor.querySelectorAll('[data-cmd]').forEach((button) => button.addEventListener('click', () => {
            content.focus();
            const cmd = button.dataset.cmd;
            if (cmd === 'createLink') {
                const url = window.prompt('Link address (https://...)', 'https://');
                if (url && /^(https?:|mailto:|tel:)/i.test(url)) document.execCommand('createLink', false, url);
            } else {
                document.execCommand(cmd, false, button.dataset.value ?? null);
            }
            sync();
        }));
        editor.closest('form')?.addEventListener('submit', sync);
    });

    // Placeholders go into whichever field was used last (the message by default).
    let target = document.querySelector('[data-rich-content]') ?? document.querySelector('[data-placeholder-target]');
    document.querySelectorAll('[data-placeholder-target]').forEach((field) => field.addEventListener('focus', () => { target = field; }));
    document.querySelectorAll('[data-insert]').forEach((button) => button.addEventListener('click', () => {
        if (!target) return;
        const text = button.dataset.insert;
        target.focus();
        if (target.isContentEditable) {
            document.execCommand('insertText', false, text);
            target.dispatchEvent(new Event('input'));
        } else {
            const start = target.selectionStart ?? target.value.length;
            target.setRangeText(text, start, target.selectionEnd ?? start, 'end');
            target.dispatchEvent(new Event('input', { bubbles: true }));
        }
    }));

    // SMS length: 160 characters (153 per part) in the GSM alphabet, otherwise 70 (67).
    const gsm = /^[@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !"#¤%&'()*+,\-./0-9:;<=>?¡A-ZÄÖÑÜ§¿a-zäöñüà]*$/;
    document.querySelectorAll('[data-sms-text]').forEach((area) => {
        const out = area.closest('div')?.parentElement?.querySelector('[data-sms-count]') ?? document.querySelector('[data-sms-count]');
        const update = () => {
            const text = area.value;
            const plain = gsm.test(text);
            const single = plain ? 160 : 70;
            const multi = plain ? 153 : 67;
            const parts = text.length === 0 ? 0 : text.length <= single ? 1 : Math.ceil(text.length / multi);
            if (out) out.textContent = text.length === 0 ? 'Empty: the message is used as plain text' : `${text.length} characters, ${parts} SMS part${parts === 1 ? '' : 's'}${plain ? '' : ' (special characters make parts shorter)'}`;
        };
        area.addEventListener('input', update);
        update();
    });

    const form = document.querySelector('form[data-compose]');
    if (!form) return;
    const modes = [...form.querySelectorAll('[data-mode]')];
    const audiences = [...form.querySelectorAll('[data-audience]')];
    const mode = () => modes.find((m) => m.checked)?.value ?? 'pick';
    const audience = () => audiences.find((a) => a.checked);

    // Show the panels of the chosen mode and audience; hidden inputs are disabled so they aren't sent.
    const layout = () => {
        const m = mode();
        const code = m === 'list' ? 'list' : audience()?.value;
        form.querySelectorAll('[data-mode-panel]').forEach((panel) => {
            const show = panel.dataset.modePanel.split(' ').includes(m);
            panel.hidden = !show;
        });
        form.querySelectorAll('[data-audience-panel]').forEach((panel) => {
            const show = m === 'everyone' && panel.dataset.audiencePanel === audience()?.value;
            panel.hidden = !show;
            panel.querySelectorAll('select').forEach((s) => { s.disabled = !show; });
        });
        form.querySelectorAll('[data-placeholder-set]').forEach((set) => { set.hidden = set.dataset.placeholderSet !== code; });
    };

    // Channel sections: email and in-app share the subject and message; SMS and WhatsApp have their own.
    const sections = () => {
        const on = [...form.querySelectorAll('[data-channel]')].filter((c) => c.checked).map((c) => c.value);
        form.querySelectorAll('[data-channel-section]').forEach((section) => {
            section.hidden = !section.dataset.channelSection.split(' ').some((c) => on.includes(c));
        });
    };

    const whenLater = form.querySelector('[data-when-later]');
    const sendLabel = form.querySelector('[data-send-label]');
    const when = () => {
        const later = form.querySelector('[data-when]:checked')?.value === 'later';
        if (whenLater) whenLater.hidden = !later;
        const button = sendLabel?.closest('button');
        if (sendLabel && button) sendLabel.textContent = later ? button.dataset.sendLabelLater : button.dataset.sendLabelNow;
    };

    // Reach: how many people the current choice reaches, refreshed as it changes.
    let timer;
    const set = (name, value) => { const el = form.querySelector(`[data-reach-${name}]`); if (el) el.textContent = value; };
    const reach = () => {
        clearTimeout(timer);
        timer = setTimeout(async () => {
            const data = new FormData(form);
            data.delete('Action');
            try {
                const response = await fetch(form.dataset.reachUrl, { method: 'POST', body: data, headers: { Accept: 'application/json' }, credentials: 'same-origin' });
                const result = await response.json();
                const problems = form.querySelector('[data-reach-problems]');
                problems?.replaceChildren();
                if (result.error) {
                    set('people', '–'); set('email', '–'); set('phone', '–'); set('account', '–'); set('label', result.error);
                    return;
                }
                set('people', result.people.toLocaleString()); set('email', result.withEmail.toLocaleString());
                set('phone', result.withPhone.toLocaleString()); set('account', result.withAccount.toLocaleString()); set('label', result.label);
                (result.problems ?? []).slice(0, 5).forEach((p) => { const li = document.createElement('li'); li.textContent = p; problems?.append(li); });
                if (mode() === 'list') {
                    const listSet = form.querySelector('[data-placeholder-set="list"] .placeholder-chips');
                    listSet?.replaceChildren(...result.fields.map((f) => {
                        const b = document.createElement('button');
                        b.type = 'button'; b.className = 'chip'; b.dataset.insert = `{{${f.name}}}`; b.textContent = `+ ${f.label}`;
                        b.addEventListener('click', () => { if (target) { target.focus(); if (target.isContentEditable) { document.execCommand('insertText', false, b.dataset.insert); target.dispatchEvent(new Event('input')); } else { target.setRangeText(b.dataset.insert, target.selectionStart ?? 0, target.selectionEnd ?? 0, 'end'); } } });
                        return b;
                    }));
                }
            } catch { /* the server checks again on send */ }
        }, 350);
    };

    // Picker: search the chosen audience and add people as chips.
    const picker = form.querySelector('[data-picker]');
    const input = picker?.querySelector('[data-picker-input]');
    const results = picker?.querySelector('[data-picker-results]');
    const chips = picker?.querySelector('[data-picker-chips]');
    const chip = (key, name) => {
        if (chips.querySelector(`[data-key="${CSS.escape(key)}"]`)) return;
        const span = document.createElement('span');
        span.className = 'chip picker-chip'; span.dataset.key = key;
        const hidden = document.createElement('input');
        hidden.type = 'hidden'; hidden.name = 'Keys'; hidden.value = key;
        const remove = document.createElement('button');
        remove.type = 'button'; remove.className = 'picker-remove'; remove.setAttribute('aria-label', `Remove ${name}`); remove.textContent = '×';
        remove.addEventListener('click', () => { span.remove(); reach(); });
        span.append(hidden, document.createTextNode(name + ' '), remove);
        chips.append(span);
    };
    chips?.querySelectorAll('[data-picker-remove]').forEach((b) => b.addEventListener('click', () => { b.closest('.picker-chip')?.remove(); reach(); }));
    let searchTimer;
    input?.addEventListener('input', () => {
        clearTimeout(searchTimer);
        const term = input.value.trim();
        if (term.length < 2) { results.hidden = true; return; }
        searchTimer = setTimeout(async () => {
            const url = `${audience()?.dataset.searchUrl}?term=${encodeURIComponent(term)}`;
            try {
                const found = await (await fetch(url, { headers: { Accept: 'application/json' }, credentials: 'same-origin' })).json();
                results.replaceChildren(...(found.length ? found : [{ key: '', name: 'Nobody matches', detail: '' }]).map((m) => {
                    const li = document.createElement('li');
                    li.setAttribute('role', 'option');
                    if (!m.key) { li.className = 'is-empty'; li.textContent = m.name; return li; }
                    const name = document.createElement('span'); name.className = 'picker-name'; name.textContent = m.name;
                    const detail = document.createElement('span'); detail.className = 'picker-detail'; detail.textContent = m.detail;
                    li.append(name, detail); li.tabIndex = 0;
                    const pick = () => { chip(m.key, m.name); input.value = ''; results.hidden = true; input.focus(); reach(); };
                    li.addEventListener('click', pick);
                    li.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); pick(); } });
                    return li;
                }));
                results.hidden = false;
            } catch { results.hidden = true; }
        }, 250);
    });
    input?.addEventListener('keydown', (e) => { if (e.key === 'Enter') e.preventDefault(); if (e.key === 'ArrowDown') results.querySelector('li[tabindex]')?.focus(); });

    form.querySelector('[data-template-pick]')?.addEventListener('change', (e) => {
        const id = e.target.value;
        window.location.href = id ? `${e.target.dataset.templateUrl}?template=${encodeURIComponent(id)}` : e.target.dataset.templateUrl;
    });

    modes.forEach((m) => m.addEventListener('change', () => { layout(); reach(); }));
    audiences.forEach((a) => a.addEventListener('change', () => { chips?.replaceChildren(); layout(); reach(); }));
    form.querySelectorAll('[data-filter], [data-reach-input]').forEach((el) => el.addEventListener('change', reach));
    form.querySelector('#ListText')?.addEventListener('input', reach);
    form.querySelectorAll('[data-channel]').forEach((c) => c.addEventListener('change', sections));
    form.querySelectorAll('[data-when]').forEach((w) => w.addEventListener('change', when));
    layout(); sections(); when(); reach();
})();

// A drawer or dialog that must open when the page loads (e.g. its form came back with errors).
(() => {
    document.querySelectorAll('[data-open-on-load="true"]').forEach((el) => {
        if (!window.bootstrap) return;
        const Kind = el.classList.contains('offcanvas') ? window.bootstrap.Offcanvas : window.bootstrap.Modal;
        Kind.getOrCreateInstance(el).show();
    });
})();

// File previews in a drawer: <button data-preview="pdf|image|text|html|video|audio" data-preview-src="...">.
// Text and HTML frames are sandboxed here as well as by the preview response's own CSP.
(() => {
    const drawer = document.getElementById('preview-drawer');
    if (!drawer || !window.bootstrap) return;
    const stage = drawer.querySelector('[data-preview-stage]');
    const offcanvas = bootstrap.Offcanvas.getOrCreateInstance(drawer);
    const make = (kind, src, name) => {
        if (kind === 'image') { const img = document.createElement('img'); img.src = src; img.alt = name; return img; }
        if (kind === 'video' || kind === 'audio') { const media = document.createElement(kind); media.src = src; media.controls = true; return media; }
        const frame = document.createElement('iframe');
        frame.title = `Preview of ${name}`;
        if (kind !== 'pdf') frame.setAttribute('sandbox', '');
        frame.src = src;
        return frame;
    };
    document.addEventListener('click', (event) => {
        const button = event.target.closest('[data-preview]');
        if (!button) return;
        const data = button.dataset;
        drawer.querySelector('[data-preview-title]').textContent = data.previewName || 'Preview';
        drawer.querySelector('[data-preview-file]').textContent = data.previewFilename || '';
        const tile = drawer.querySelector('[data-preview-tile]');
        tile.className = `file-tile ${data.previewTone || ''}`;
        const use = tile.querySelector('use');
        if (use) use.setAttribute('href', `${use.getAttribute('href').split('#')[0]}#${data.previewIcon || 'file'}`);
        drawer.querySelector('[data-preview-open]').href = data.previewOpenUrl || '#';
        drawer.querySelector('[data-preview-download]').href = data.previewDownloadUrl || '#';
        stage.classList.toggle('is-media', data.preview === 'audio');
        stage.replaceChildren(make(data.preview, data.previewSrc, data.previewName || ''));
        offcanvas.show();
    });
    // Stop media and drop the frame when the drawer closes.
    drawer.addEventListener('hidden.bs.offcanvas', () => stage.replaceChildren());
})();

// <input type="file" data-fill-title="#title">: an empty title takes the chosen file's name, without the extension.
document.querySelectorAll('input[type="file"][data-fill-title]').forEach((input) => input.addEventListener('change', () => {
    const target = document.querySelector(input.dataset.fillTitle);
    const file = input.files?.[0];
    if (!target || !file || target.value.trim()) return;
    target.value = file.name.replace(/\.[^.]+$/, '').replace(/[_-]+/g, ' ').trim();
}));

// <button data-check-all="#group">: ticks every checkbox in the group (or clears them when all are ticked); groups
// with [data-check-count] keep their "n of m" up to date.
(() => {
    const refresh = (group) => {
        const boxes = [...group.querySelectorAll('input[type="checkbox"]')];
        const ticked = boxes.filter((b) => b.checked).length;
        const count = group.querySelector('[data-check-count]');
        if (count) count.textContent = `${ticked} of ${boxes.length}`;
        const button = group.querySelector('[data-check-all]');
        if (button) button.textContent = ticked === boxes.length ? 'Clear all' : 'Select all';
    };
    document.querySelectorAll('[data-check-all]').forEach((button) => {
        const group = document.querySelector(button.dataset.checkAll);
        if (!group) return;
        refresh(group);
        group.addEventListener('change', () => refresh(group));
        button.addEventListener('click', () => {
            const boxes = [...group.querySelectorAll('input[type="checkbox"]')];
            const all = boxes.every((b) => b.checked);
            boxes.forEach((b) => { b.checked = !all; });
            refresh(group);
        });
    });
})();

// Feature flags: the targeting choices show only for "Some organisations".
(() => {
    document.querySelectorAll('form[data-flag-form]').forEach((form) => {
        const targeting = form.querySelector('[data-flag-targeting]');
        if (!targeting) return;
        const layout = () => {
            const mode = [...form.querySelectorAll('[data-flag-mode]')].find((m) => m.checked)?.value;
            targeting.hidden = mode !== 'Targeted';
        };
        form.querySelectorAll('[data-flag-mode]').forEach((m) => m.addEventListener('change', layout));
        layout();
    });
})();

// Payments: post the gateway's form by itself, and re-check a payment that is still waiting.
(() => {
    document.querySelector('form[data-submit-on-load]')?.requestSubmit();
    const seconds = Number(document.querySelector('[data-reload-in]')?.dataset.reloadIn);
    if (seconds > 0) setTimeout(() => window.location.reload(), seconds * 1000);
})();
