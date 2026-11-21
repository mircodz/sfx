import { createContext, useCallback, useContext, useEffect, useRef, useState } from "react";
import { useView } from "./view";

/** Bumped after every change (an action, power on or off) so every view on screen loads again. */
export const RefreshContext = createContext({ generation: 0, bump: () => {} });

export type Resource<T> = { data?: T; error?: string; loading: boolean; at?: Date; reload: () => void };

const refreshMs = 10_000;

/**
 * Loads `load()` and keeps it fresh: again 10 seconds after each load finishes (so slow loads never pile up), only
 * while the browser tab is visible (and at once when it becomes visible again after missing a refresh), after every
 * change, and whenever `key` changes. Keeps showing the last data while it reloads.
 */
export function useResource<T>(name: string, load: () => Promise<T>, poll = true): Resource<T> {
    const { generation } = useContext(RefreshContext);
    // Another view is other data: never show one view's list under another while it loads.
    const view = useView();
    const key = `${name}|${view ?? ""}`;
    const [state, setState] = useState<{ key: string; data?: T; error?: string; loading: boolean; at?: Date }>({ key, loading: true });
    const [tick, setTick] = useState(0);
    const loadRef = useRef(load);
    loadRef.current = load;
    const due = useRef(false);

    useEffect(() => {
        let live = true;
        let timer: ReturnType<typeof setTimeout> | undefined;
        due.current = false;
        // A new key starts empty; the same key keeps its data on screen without an extra "loading" render.
        setState((s) => (s.key === key ? s : { key, loading: true }));
        loadRef
            .current()
            .then((data) => live && setState({ key, data, loading: false, at: new Date() }))
            .catch((e: unknown) => live && setState((s) => ({ ...s, key, error: e instanceof Error ? e.message : String(e), loading: false })))
            .finally(() => {
                if (!live || !poll) return;
                timer = setTimeout(() => {
                    if (document.visibilityState === "visible") setTick((t) => t + 1);
                    else due.current = true;
                }, refreshMs);
            });
        return () => {
            live = false;
            clearTimeout(timer);
        };
    }, [key, generation, tick, poll]);

    // Back on a tab that skipped a refresh while hidden: refresh now rather than up to 10 seconds later.
    useEffect(() => {
        const onVisible = () => {
            if (document.visibilityState === "visible" && due.current) setTick((t) => t + 1);
        };
        document.addEventListener("visibilitychange", onVisible);
        return () => document.removeEventListener("visibilitychange", onVisible);
    }, []);

    const reload = useCallback(() => setTick((t) => t + 1), []);
    const current = state.key === key ? state : { loading: true };
    return { ...current, reload };
}
