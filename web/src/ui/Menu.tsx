import { useEffect, useId, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import { IconButton } from "./Button";
import "./Menu.css";

export type MenuItem = { id: string; label: string; danger?: boolean; onPick: () => void };

/** Danger items come last, after a divider. Arrows move, Esc closes. */
export function Menu({ label, items }: { label: string; items: MenuItem[] }) {
    const [open, setOpen] = useState(false);
    const anchor = useRef<HTMLDivElement>(null);
    const list = useRef<HTMLDivElement>(null);
    const id = useId();
    const ordered = [...items.filter((i) => !i.danger), ...items.filter((i) => i.danger)];
    const firstDanger = ordered.findIndex((i) => i.danger);

    useEffect(() => {
        if (!open) return;
        list.current?.querySelector<HTMLButtonElement>("button")?.focus();
        const close = (e: MouseEvent) => !anchor.current?.contains(e.target as Node) && setOpen(false);
        document.addEventListener("mousedown", close);
        return () => document.removeEventListener("mousedown", close);
    }, [open]);

    const onKey = (e: KeyboardEvent) => {
        const buttons = [...(list.current?.querySelectorAll<HTMLButtonElement>("button") ?? [])];
        const at = buttons.indexOf(document.activeElement as HTMLButtonElement);
        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
            e.preventDefault();
            buttons[(at + (e.key === "ArrowDown" ? 1 : buttons.length - 1)) % buttons.length]?.focus();
        } else if (e.key === "Escape") {
            setOpen(false);
            anchor.current?.querySelector("button")?.focus();
        }
    };

    return (
        <div className="menu-anchor" ref={anchor}>
            <IconButton icon="dots" label={label} aria-haspopup="menu" aria-expanded={open} aria-controls={id} onClick={() => setOpen(!open)} />
            {open && (
                <div
                    ref={list}
                    id={id}
                    className="menu"
                    role="menu"
                    onKeyDown={onKey}
                    onBlur={(e) => !anchor.current?.contains(e.relatedTarget as Node | null) && setOpen(false)}
                >
                    {ordered.map((item, i) => (
                        <div key={item.id}>
                            {i === firstDanger && i > 0 && <div className="menu-divider" role="separator" />}
                            <button
                                type="button"
                                role="menuitem"
                                className={`menu-item ${item.danger ? "text-danger" : ""}`}
                                onClick={() => {
                                    setOpen(false);
                                    // Focus back on "More actions" first, so a dialog opened from here returns it there.
                                    anchor.current?.querySelector<HTMLButtonElement>("button")?.focus();
                                    item.onPick();
                                }}
                            >
                                {item.label}
                            </button>
                        </div>
                    ))}
                </div>
            )}
        </div>
    );
}
