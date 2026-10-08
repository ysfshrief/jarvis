// Thin client for the JARVIS runtime's local API. Every request carries the runtime token,
// which the desktop shell passes in the URL fragment (#token=...) and we keep in sessionStorage.
const TOKEN_KEY = "jarvis.token";
export function initToken() {
    // The desktop shell opens "#token=...&page=assistant"; keep the token, then route to the page.
    const raw = window.location.hash.replace(/^#/, "");
    if (!raw.startsWith("/")) {
        const params = new URLSearchParams(raw);
        const fromHash = params.get("token");
        if (fromHash) {
            sessionStorage.setItem(TOKEN_KEY, fromHash);
            const page = params.get("page");
            history.replaceState(null, "", window.location.pathname + window.location.search + (page ? `#/${page}` : ""));
        }
    }
    return sessionStorage.getItem(TOKEN_KEY);
}
export function setToken(token) {
    sessionStorage.setItem(TOKEN_KEY, token.trim());
}
export function getToken() {
    return sessionStorage.getItem(TOKEN_KEY);
}
export class ApiError extends Error {
    status;
    constructor(status, message) {
        super(message);
        this.status = status;
    }
}
const authListeners = new Set();
export function onAuthProblem(l) {
    authListeners.add(l);
    return () => authListeners.delete(l);
}
export async function api(method, path, body) {
    const res = await fetch(`./api${path}`, {
        method,
        headers: {
            "X-Jarvis-Token": getToken() ?? "",
            ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
        },
        body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    if (res.status === 401 || res.status === 423)
        authListeners.forEach((l) => l(res.status));
    const text = await res.text();
    const data = text ? safeJson(text) : null;
    if (!res.ok) {
        const msg = (data && typeof data === "object" && "error" in data ? String(data.error) : null) ?? `${res.status} ${res.statusText}`;
        throw new ApiError(res.status, msg);
    }
    return data;
}
function safeJson(text) {
    try {
        return JSON.parse(text);
    }
    catch {
        return text;
    }
}
export const get = (p) => api("GET", p);
export const post = (p, b) => api("POST", p, b ?? {});
export const put = (p, b) => api("PUT", p, b ?? {});
export const del = (p) => api("DELETE", p);
