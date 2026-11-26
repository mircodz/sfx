import { useState } from "react";
import type { ReactNode } from "react";
import { Icon } from "./Icon";
import "./Disclosure.css";

export function Disclosure({
    label,
    aside,
    initiallyOpen = false,
    className = "",
    children,
}: {
    label: ReactNode;
    aside?: ReactNode;
    initiallyOpen?: boolean;
    className?: string;
    children: ReactNode;
}) {
    const [open, setOpen] = useState(initiallyOpen);
    return (
        <div className={className}>
            <button type="button" className="disclosure" aria-expanded={open} onClick={() => setOpen(!open)}>
                <Icon name="chevron-down" className="disclosure-chevron" />
                <span>{label}</span>
                {aside && <span className="disclosure-aside">{aside}</span>}
            </button>
            {open && <div className="disclosure-body">{children}</div>}
        </div>
    );
}
