import { createContext, useContext, useEffect, useRef, useState } from "react";
import { api } from "./api";
import type { Operation } from "./types";

export const OperationsContext = createContext<Operation[]>([]);

export const useOperations = () => useContext(OperationsContext);

/**
 * Polls every 3 s while an operation runs, else every 15 s (another tab may start one). Calls `onSettled` once per
 * operation that finishes while watched.
 */
export function useOperationsPoll(generation: number, onSettled: (op: Operation) => void) {
    const [ops, setOps] = useState<Operation[]>([]);
    const seen = useRef(new Map<string, Operation["state"]>());
    const settled = useRef(onSettled);
    settled.current = onSettled;
    const running = ops.some((o) => o.state === "running");

    useEffect(() => {
        let live = true;
        const load = () =>
            api.operations().then(
                (list) => {
                    if (!live) return;
                    for (const op of list) {
                        const before = seen.current.get(op.id);
                        if (before === "running" && op.state !== "running") settled.current(op);
                        seen.current.set(op.id, op.state);
                    }
                    setOps(list);
                },
                () => {},
            );
        load();
        // Hidden tabs don't need to know; they catch up when shown.
        const id = setInterval(() => document.visibilityState === "visible" && load(), running ? 3000 : 15000);
        const onVisible = () => document.visibilityState === "visible" && load();
        document.addEventListener("visibilitychange", onVisible);
        return () => {
            live = false;
            clearInterval(id);
            document.removeEventListener("visibilitychange", onVisible);
        };
    }, [running, generation]);

    return ops;
}
