import type { ReactNode } from "react";
import { clock } from "../lib/format";
import { Notice } from "./Notice";
import "./Load.css";

type Shape = "page" | "rows" | "lines";

/** Placeholder shapes in the content's layout while it first loads; no spinner, no animation. */
export function Skeleton({ shape = "rows" }: { shape?: Shape }) {
    return (
        <div aria-busy="true" aria-label="Loading">
            {shape === "page" && (
                <>
                    <div className="skeleton skeleton-title" />
                    <div className="skeleton skeleton-line short" />
                    <div className="skeleton skeleton-tabs" />
                </>
            )}
            {shape === "lines"
                ? Array.from({ length: 8 }, (_, i) => <div key={i} className="skeleton skeleton-line" />)
                : Array.from({ length: 4 }, (_, i) => <div key={i} className="skeleton skeleton-row" />)}
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
    resource: { data?: T; error?: string; at?: Date };
    shape?: Shape;
    children: (data: T) => ReactNode;
}) {
    if (resource.data === undefined) {
        return resource.error ? <Notice tone="danger">{resource.error}</Notice> : <Skeleton shape={shape} />;
    }
    return (
        <>
            {resource.error && (
                <Notice tone="danger">
                    {resource.error.replace(/\.$/, "")}. Showing data from {resource.at ? clock(resource.at) : "earlier"}.
                </Notice>
            )}
            {children(resource.data)}
        </>
    );
}
