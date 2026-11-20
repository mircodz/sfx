import { useEffect } from "react";
import type { KeyboardEvent, ReactNode } from "react";
import { toneOf } from "../lib/health";
import { useQueryParam } from "../lib/router";
import type { Health } from "../lib/types";
import "./Tabs.css";

export type TabDef<T extends string> = { id: T; label: ReactNode; hidden?: boolean };

/** The selected tab lives in the URL (?tab=health), so links and reloads keep it. The first tab is the default. */
export function useTab<T extends string>(tabs: readonly T[]): [T, (tab: T) => void] {
    const [value, set] = useQueryParam<T>("tab", tabs[0]);
    return [tabs.includes(value) ? value : tabs[0], set];
}

/** Tabs and the panel of the selected one; arrow keys move between tabs. */
export function Tabs<T extends string>({
    tabs,
    value,
    onChange,
    children,
}: {
    tabs: TabDef<T>[];
    value: T;
    onChange: (tab: T) => void;
    children: ReactNode;
}) {
    const shown = tabs.filter((t) => !t.hidden);
    // A link (or an old bookmark) to a tab this entity doesn't have lands on the first one it does.
    const missing = !shown.some((t) => t.id === value);
    useEffect(() => {
        if (missing && shown[0]) onChange(shown[0].id);
    });
    const onKey = (e: KeyboardEvent, i: number) => {
        const target =
            e.key === "ArrowRight" ? (i + 1) % shown.length : e.key === "ArrowLeft" ? (i + shown.length - 1) % shown.length : e.key === "Home" ? 0 : e.key === "End" ? shown.length - 1 : -1;
        if (target < 0) return;
        e.preventDefault();
        const next = shown[target];
        onChange(next.id);
        document.getElementById(`tab-${next.id}`)?.focus();
    };
    return (
        <>
            <div className="tabs" role="tablist">
                {shown.map((t, i) => (
                    <button
                        key={t.id}
                        id={`tab-${t.id}`}
                        type="button"
                        role="tab"
                        className="tab"
                        aria-selected={value === t.id}
                        aria-controls={`panel-${t.id}`}
                        tabIndex={value === t.id ? 0 : -1}
                        onClick={() => onChange(t.id)}
                        onKeyDown={(e) => onKey(e, i)}
                    >
                        {t.label}
                    </button>
                ))}
            </div>
            <div role="tabpanel" id={`panel-${value}`} aria-labelledby={`tab-${value}`} className="tab-panel">
                {missing ? null : children}
            </div>
        </>
    );
}

/** "Health", or "Health · Error" in the status color when it isn't healthy. */
export function HealthTab({ health }: { health: Health }) {
    return (
        <>
            Health
            {(health === "Warning" || health === "Error") && <span className={`text-${toneOf(health)}`}> · {health}</span>}
        </>
    );
}

/** "Replicas 3". */
export function CountTab({ label, count }: { label: string; count?: number }) {
    return (
        <>
            {label}
            {count !== undefined && <span className="tab-count">{count}</span>}
        </>
    );
}
