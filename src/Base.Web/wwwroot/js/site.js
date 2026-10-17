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
