import type { ReactNode } from "react";
import "./Chips.css";

/** Mutually exclusive options as chips: a filter, a layout, a mode. */
export function Chips<T extends string>({
    options,
    value,
    onChange,
    label,
    labels,
}: {
    options: readonly T[];
    value: T;
    onChange: (value: T) => void;
    label: string;
    labels?: Partial<Record<T, ReactNode>>;
}) {
    return (
        <div className="chips" role="group" aria-label={label}>
            {options.map((o) => (
                <button key={o} type="button" className="chip" aria-pressed={o === value} onClick={() => onChange(o)}>
                    {labels?.[o] ?? o}
                </button>
            ))}
        </div>
    );
}
