import type { ReactNode } from "react";
import { Icon } from "./Icon";
import "./Toolbar.css";

/** Above a list: search first, then filter and layout chips. */
export function Toolbar({ children }: { children: ReactNode }) {
    return <div className="toolbar">{children}</div>;
}

export function SearchBox({ value, onChange, placeholder }: { value: string; onChange: (value: string) => void; placeholder: string }) {
    return (
        <label className="search">
            <Icon name="search" className="search-icon" />
            <span className="sr-only">{placeholder}</span>
            <input
                type="search"
                placeholder={placeholder}
                value={value}
                onChange={(e) => onChange(e.target.value)}
                onKeyDown={(e) => e.key === "Escape" && onChange("")}
            />
        </label>
    );
}
