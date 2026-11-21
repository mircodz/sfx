import { useSyncExternalStore } from "react";

/**
 * The view (a named filter on applications, from ~/.sfx/config.toml) this tab looks through. Per tab, in the address
 * bar as ?view=, so a reload or a shared link keeps it: absent follows the context's default, empty is "everything".
 */
let chosen: string | null | undefined = initial();
let fallback: string | null = null;
let known: string[] | undefined;
const listeners = new Set<() => void>();

function initial(): string | null | undefined {
    if (typeof location === "undefined") return undefined;
    const params = new URLSearchParams(location.search);
    return params.has("view") ? params.get("view") || null : undefined;
}

function emit() {
    for (const l of listeners) l();
}

function writeUrl() {
    const params = new URLSearchParams(location.search);
    if (chosen === undefined) params.delete("view");
    else params.set("view", chosen ?? "");
    const search = params.toString();
    history.replaceState(history.state, "", `${location.pathname}${search ? `?${search}` : ""}${location.hash}`);
}

/** The view in effect: the one chosen in this tab, else the context's default, else none. */
export function currentView(): string | null {
    const view = chosen === undefined ? fallback : chosen;
    return view && (!known || known.includes(view)) ? view : null;
}

/** Choose a view for this tab; null for everything. */
export function chooseView(view: string | null) {
    chosen = view;
    writeUrl();
    emit();
}

/** What the session says: the views there are and the context's default. A view that no longer exists is dropped. */
export function syncViews(views: string[], defaultView: string | null) {
    const before = currentView();
    known = views;
    fallback = defaultView;
    if (typeof chosen === "string" && !views.includes(chosen)) {
        chosen = undefined;
        writeUrl();
    }
    if (currentView() !== before) emit();
}

/** Another context: back to its default view. */
export function resetView() {
    if (chosen === undefined) return;
    chosen = undefined;
    writeUrl();
    emit();
}

function subscribe(listener: () => void) {
    listeners.add(listener);
    return () => listeners.delete(listener);
}

export function useView(): string | null {
    return useSyncExternalStore(subscribe, currentView);
}

// Requests for what a view filters: lists of applications and what hangs under them, and a node's replicas.
const scoped = /^\/(applications|replicas|upgrades|events|overview)(\?|$)|^\/nodes\/[^/?]+(\/packages)?(\?|$)/;

/** A request path with the view in effect added, where the server filters by it. */
export function withView(method: string, path: string) {
    return scopedPath(method, path, currentView());
}

export function scopedPath(method: string, path: string, view: string | null) {
    // One entity's own events are for a page opened directly, which a view doesn't hide either.
    if (!view || method !== "GET" || !scoped.test(path) || /[?&]entity=/.test(path)) return path;
    return `${path}${path.includes("?") ? "&" : "?"}view=${encodeURIComponent(view)}`;
}
