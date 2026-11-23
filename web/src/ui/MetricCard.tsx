import type { ReactNode } from "react";
import "./MetricCard.css";

/** A number with its label; links somewhere when given an href. */
export function MetricCard({ label, value, note, href }: { label: string; value: ReactNode; note?: ReactNode; href?: string }) {
    const body = (
        <>
            <span className="metric-label">{label}</span>
            <span className="metric-value">{value}</span>
            {note && <span className="metric-note">{note}</span>}
        </>
    );
    return href ? (
        <a className="metric" href={href}>
            {body}
        </a>
    ) : (
        <div className="metric">{body}</div>
    );
}

export function Metrics({ children }: { children: ReactNode }) {
    return <div className="metrics">{children}</div>;
}
