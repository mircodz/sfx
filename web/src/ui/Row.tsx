import type { ReactNode } from "react";
import type { Status } from "../lib/types";
import { Icon } from "./Icon";
import { Pill } from "./Pill";
import "./Row.css";

type RowContent = {
    icon: string;
    name: ReactNode;
    sub?: ReactNode;
    /** Why it's unhealthy, in the status color under the facts. */
    reason?: ReactNode;
    status?: Status;
    value?: ReactNode;
};

function Summary({ icon, name, sub, reason, status, value, chevron }: RowContent & { chevron: string }) {
    return (
        <>
            <Icon name={icon} className="row-icon" />
            <span className="row-main">
                <span className="row-name">{name}</span>
                {sub && <span className="row-sub">{sub}</span>}
                {reason && <span className={`row-reason ${status === "Error" ? "text-danger" : "text-warning"}`}>{reason}</span>}
            </span>
            {value && <span className="row-value">{value}</span>}
            {status && <Pill status={status} />}
            <Icon name={chevron} className="row-chevron" />
        </>
    );
}

export function LinkRow({ href, ...content }: RowContent & { href: string }) {
    return (
        <a className="row row-summary" href={href}>
            <Summary {...content} chevron="chevron-right" />
        </a>
    );
}

export function DisclosureRow({
    id,
    open,
    onToggle,
    children,
    ...content
}: RowContent & { id: string; open: boolean; onToggle: () => void; children: ReactNode }) {
    const detailsId = `row-${id.replace(/[^\w-]/g, "_")}`;
    return (
        <div className={`row ${open ? "open" : ""}`}>
            <button type="button" className="row-summary" aria-expanded={open} aria-controls={detailsId} onClick={onToggle}>
                <Summary {...content} chevron="chevron-down" />
            </button>
            {open && (
                <div className="row-details" id={detailsId}>
                    {children}
                </div>
            )}
        </div>
    );
}

export function RowActions({ children }: { children: ReactNode }) {
    return <div className="row-actions">{children}</div>;
}
