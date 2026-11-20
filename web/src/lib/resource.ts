import { createContext, useCallback, useContext, useEffect, useRef, useState } from "react";

/** Bumped after every change (an action, power on or off) so every view on screen loads again. */
export const RefreshContext = createContext({ generation: 0, bump: () => {} });

export type Resource<T> = { data?: T; error?: string; loading: boolean; at?: Date; reload: () => void };

const refreshMs = 10_000;

/**
 * Loads `load()` and keeps it fresh: every 10 seconds while the tab is visible, after every change, and whenever
 * `key` changes. Keeps showing the last data while it reloads.
 */
export function useResource<T>(key: string, load: () => Promise<T>, poll = true): Resource<T> {
    const { generation } = useContext(RefreshContext);
    const [state, setState] = useState<{ key: string; data?: T; error?: string; loading: boolean; at?: Date }>({ key, loading: true });
    const [tick, setTick] = useState(0);
    const loadRef = useRef(load);
    loadRef.current = load;

    useEffect(() => {
        let live = true;
        setState((s) => (s.key === key ? { ...s, loading: true } : { key, loading: true }));
        loadRef
            .current()
            .then((data) => live && setState({ key, data, loading: false, at: new Date() }))
            .catch((e: unknown) => live && setState((s) => ({ ...s, key, error: e instanceof Error ? e.message : String(e), loading: false })));
        return () => {
            live = false;
        };
    }, [key, generation, tick]);

    useEffect(() => {
        if (!poll) return;
        const id = setInterval(() => document.visibilityState === "visible" && setTick((t) => t + 1), refreshMs);
        return () => clearInterval(id);
    }, [poll]);

    const reload = useCallback(() => setTick((t) => t + 1), []);
    const current = state.key === key ? state : { loading: true };
    return { ...current, reload };
}
