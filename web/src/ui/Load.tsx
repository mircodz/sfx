import type { ReactNode } from "react";
import { clock } from "../lib/format";
import { Notice } from "./Notice";
import "./Load.css";

type Shape = "page" | "dashboard" | "rows" | "lines";

/** Placeholder shapes in the content's layout while it first loads; no spinner, no animation. */
export function Skeleton({ shape = "rows" }: { shape?: Shape }) {
    return (
        <div aria-busy="true" aria-label="Loading">
            {(shape === "page" || shape === "dashboard") && (
                <>
                    <div className="skeleton skeleton-crumbs" />
                    <div className="skeleton skeleton-title" />
                    <div className="skeleton skeleton-line short" />
                    <div className="skeleton-tabs" />
                </>
            )}
            {shape === "dashboard" ? (
                <>
                    <div className="skeleton-metrics">
                        {Array.from({ length: 6 }, (_, i) => (
                            <div key={i} className="skeleton skeleton-metric" />
                        ))}
                    </div>
                    <div className="skeleton-cards">
                        {Array.from({ length: 4 }, (_, i) => (
                            <div key={i} className="skeleton skeleton-card" />
                        ))}
                    </div>
                </>
            ) : shape === "lines" ? (
                Array.from({ length: 8 }, (_, i) => <div key={i} className="skeleton skeleton-line" />)
            ) : (
                <div className="skeleton-list">
                    {Array.from({ length: 6 }, (_, i) => (
                        <div key={i} className="skeleton skeleton-row" />
                    ))}
                </div>
            )}
        </div>
    );
}

/**
 * Renders a resource: placeholders while it first loads, then the content. A failed reload keeps the last data and
 * says how old it is, rather than blanking the page.
 */
export function Load<T>({
    resource,
    shape = "page",
    children,
}: {
    resource: { data?: T; error?: string; at?: Date; loading?: boolean; reload?: () => void };
    shape?: Shape;
    children: (data: T) => ReactNode;
}) {
    if (resource.data === undefined) {
        if (!resource.error) return <Skeleton shape={shape} />;
        return (
            <Notice tone="danger" onRetry={resource.loading ? undefined : resource.reload}>
                {sentence(resource.error)}
            </Notice>
        );
    }
    return (
        <>
            {resource.error && (
                <Notice tone="danger">
                    {sentence(resource.error).replace(/\.$/, "")}. Showing data from {resource.at ? clock(resource.at) : "earlier"}.
                </Notice>
            )}
            {children(resource.data)}
        </>
    );
}

/** A failure as a sentence: what it means first when the cluster can't be reached, and a full stop. */
export function sentence(error: string) {
    const text = /[.!?]$/.test(error.trim()) ? error.trim() : `${error.trim()}.`;
    return /refused|timed out|unreachable|no such host|name or service not known|network/i.test(text) && !/^can't reach/i.test(text) ? `Can't reach the cluster. ${text}` : text;
}
