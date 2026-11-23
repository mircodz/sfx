import type { FolderColor } from "../lib/types";

/** A folder's label ("PROD") on its color, so production never looks like dev. */
export function ContextLabel({ label, color }: { label?: string; color?: FolderColor }) {
    if (!label) return null;
    return <span className={`pill tone-${color ?? "neutral"}`}>{label}</span>;
}
