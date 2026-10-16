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
