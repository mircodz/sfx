import { useEffect, useState, useSyncExternalStore } from "react";

// Hash routing: #/apps/orders/services/orders~Counter?tab=health. The path picks the page; the query holds the page's
// own state (tab, filters) so links and reloads keep it.

const changed = "sfx:query";

function readPath() {
    try {
        return location.hash.replace(/^#\/?/, "").split("?")[0].split("/").filter(Boolean).map(decodeURIComponent);
    } catch {
        // A mangled link (bad %-encoding) shouldn't take the whole app down.
        return ["not-found"];
    }
}

function readQuery() {
    return new URLSearchParams(location.hash.split("?")[1] ?? "");
}

/** The path's segments, e.g. ["apps", "orders"]; scrolls to the top when it changes. */
export function usePath() {
    const [path, setPath] = useState(readPath);
    useEffect(() => {
        let last = location.hash.split("?")[0];
        const onChange = () => {
            const next = location.hash.split("?")[0];
            if (next !== last) {
                setPath(readPath());
                window.scrollTo(0, 0);
            }
            last = next;
        };
        window.addEventListener("hashchange", onChange);
        return () => window.removeEventListener("hashchange", onChange);
    }, []);
    return path;
}

function subscribe(onChange: () => void) {
    window.addEventListener("hashchange", onChange);
    window.addEventListener(changed, onChange);
    return () => {
        window.removeEventListener("hashchange", onChange);
        window.removeEventListener(changed, onChange);
    };
}

/**
 * One query parameter as state, read straight from the URL (so back, forward, links and edits to the address all
 * show). Setting it to the default removes it from the URL.
 */
export function useQueryParam<T extends string = string>(name: string, fallback: NoInfer<T>): [T, (value: T) => void] {
    const value = (useSyncExternalStore(subscribe, () => readQuery().get(name)) as T | null) ?? fallback;
    const set = (next: T) => {
        const query = readQuery();
        if (next === fallback || next === "") query.delete(name);
        else query.set(name, next);
        const path = location.hash.split("?")[0] || "#/";
        const q = query.toString();
        history.replaceState(null, "", q ? `${path}?${q}` : path);
        window.dispatchEvent(new Event(changed));
    };
    return [value, set];
}

const seg = encodeURIComponent;

export const href = {
    overview: "#/",
    nodes: "#/nodes",
    apps: "#/apps",
    replicas: (filters: Record<string, string> = {}) => {
        const q = new URLSearchParams(filters).toString();
        return q ? `#/replicas?${q}` : "#/replicas";
    },
    upgrades: "#/upgrades",
    events: "#/events",
    activity: "#/activity",
    node: (name: string) => `#/nodes/${seg(name)}`,
    app: (id: string) => `#/apps/${seg(id)}`,
    service: (appId: string, id: string) => `#/apps/${seg(appId)}/services/${seg(id)}`,
    partition: (appId: string, serviceId: string, id: string) => `#/apps/${seg(appId)}/services/${seg(serviceId)}/partitions/${seg(id)}`,
};

/** Service Fabric names to ids: "fabric:/shop/orders" is "shop~orders". */
export function idOf(name: string) {
    return name.replace(/^fabric:\//, "").replaceAll("/", "~");
}

/** The application id of a service id: "shop~orders~Counter" belongs to "shop~orders". */
export function appIdOfService(serviceId: string) {
    return serviceId.split("~").slice(0, -1).join("~");
}
