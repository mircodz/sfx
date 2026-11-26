import { useContext } from "react";
import { api } from "../lib/api";
import { useNotice } from "../lib/notice";
import { RefreshContext } from "../lib/resource";
import type { Action } from "../lib/types";

/** Throws so a dialog can stay open on failure. */
export function useRun() {
    const { bump } = useContext(RefreshContext);
    const notify = useNotice();
    return async (action: Action, inputs: Record<string, string>, confirm?: string) => {
        const result = await api.run(action, inputs, action.confirmText ? confirm : undefined);
        notify(`${result.message}: requested.`, true);
        bump();
    };
}

/** "replica 1234 on _Node_1", "fabric:/orders", "the upgrade of fabric:/orders". */
export function subject(t: Record<string, string>) {
    if (t.replicaId) return `${t.role === "Instance" ? "instance" : "replica"} ${t.replicaId} on ${t.node}`;
    if (t.kind === "cluster" || t.kind === "application") return `the upgrade of ${t.name}`;
    return t.name ?? t.node ?? (t.partitionId ? `partition ${t.partitionId}` : "");
}

export function errorText(err: unknown) {
    return err instanceof Error ? err.message : String(err);
}
