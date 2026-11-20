import { useId, useRef, useState } from "react";
import type { KeyboardEvent, ReactNode } from "react";
import "./Combobox.css";

export type Option = { value: string; label: ReactNode; hint?: ReactNode; disabled?: boolean; search: string };

/**
 * A text box that suggests matching options as you type (arrow keys to move, Enter to pick, Esc to close). Disabled
 * options are listed with their hint, so it's clear why something can't be picked.
 */
export function Combobox({
    label,
    placeholder,
    options,
    onPick,
    limit = 8,
}: {
    label: string;
    placeholder: string;
    options: Option[];
    onPick: (value: string) => void;
    limit?: number;
}) {
    const [query, setQuery] = useState("");
    const [open, setOpen] = useState(false);
    const [active, setActive] = useState(0);
    const input = useRef<HTMLInputElement>(null);
    const id = useId();

    const q = query.toLowerCase().trim();
    const matches = options.filter((o) => !q || o.search.toLowerCase().includes(q));
    // Names starting with the query first, then the rest; pickable before disabled.
    const ranked = matches
        .sort((a, b) => Number(!!a.disabled) - Number(!!b.disabled) || Number(!a.search.toLowerCase().startsWith(q)) - Number(!b.search.toLowerCase().startsWith(q)))
        .slice(0, limit);
    const shown = open && query !== "" ? ranked : [];

    const pick = (o: Option | undefined) => {
        if (!o || o.disabled) return;
        onPick(o.value);
        setQuery("");
        setOpen(false);
        input.current?.focus();
    };

    const onKey = (e: KeyboardEvent) => {
        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
            e.preventDefault();
            setOpen(true);
            setActive((a) => (shown.length ? (a + (e.key === "ArrowDown" ? 1 : shown.length - 1)) % shown.length : 0));
        } else if (e.key === "Enter") {
            e.preventDefault();
            pick(shown[active]);
        } else if (e.key === "Escape" && open) {
            e.stopPropagation();
            setOpen(false);
        }
    };

    return (
        <div className="combobox">
            <label className="field-label" htmlFor={id}>
                {label}
            </label>
            <input
                ref={input}
                id={id}
                role="combobox"
                aria-expanded={shown.length > 0}
                aria-controls={`${id}-list`}
                aria-activedescendant={shown.length ? `${id}-${active}` : undefined}
                aria-autocomplete="list"
                autoComplete="off"
                spellCheck={false}
                placeholder={placeholder}
                value={query}
                onChange={(e) => {
                    setQuery(e.target.value);
                    setOpen(true);
                    setActive(0);
                }}
                onKeyDown={onKey}
                onBlur={() => setTimeout(() => setOpen(false), 120)}
            />
            {shown.length > 0 && (
                <ul className="combobox-list" id={`${id}-list`} role="listbox">
                    {shown.map((o, i) => (
                        <li
                            key={o.value}
                            id={`${id}-${i}`}
                            role="option"
                            aria-selected={i === active}
                            aria-disabled={o.disabled || undefined}
                            className="combobox-option"
                            onMouseDown={(e) => {
                                e.preventDefault();
                                pick(o);
                            }}
                            onMouseEnter={() => setActive(i)}
                        >
                            <span className="combobox-label">{o.label}</span>
                            {o.hint && <span className="combobox-hint">{o.hint}</span>}
                        </li>
                    ))}
                    {matches.length > shown.length && <li className="combobox-more">{matches.length - shown.length} more; keep typing to narrow down.</li>}
                </ul>
            )}
            {open && query !== "" && matches.length === 0 && <p className="field-help">Nothing matches "{query}".</p>}
        </div>
    );
}
