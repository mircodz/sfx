import { useEffect, useSyncExternalStore } from "react";

/** The current page's refresh, shown in the top bar. */
export type PageRefresh = { reload: () => void; at?: Date };

let current: PageRefresh | null = null;
const listeners = new Set<() => void>();

function set(next: PageRefresh | null) {
    current = next;
    for (const l of listeners) l();
}

/** A page offers its refresh to the top bar while it's shown. */
export function useOfferRefresh(reload: (() => void) | undefined, at: Date | undefined) {
    useEffect(() => {
        if (!reload) return;
        set({ reload, at });
        return () => {
            if (current?.reload === reload) set(null);
        };
    }, [reload, at]);
}

export function usePageRefresh(): PageRefresh | null {
    return useSyncExternalStore(
        (l) => {
            listeners.add(l);
            return () => listeners.delete(l);
        },
        () => current,
    );
}
