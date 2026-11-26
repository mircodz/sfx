import { useState } from "react";
import type { ReactNode } from "react";
import { Button } from "./Button";
import "./ShowMore.css";

/** Paged so a cluster with thousands of something stays fast; search and filters still cover everything. */
export function useLimited<T>(items: T[], step = 200): { visible: T[]; more: ReactNode } {
    const [limit, setLimit] = useState(step);
    const visible = items.length > limit ? items.slice(0, limit) : items;
    const left = items.length - visible.length;
    const more =
        left > 0 ? (
            <div className="show-more">
                <Button onClick={() => setLimit(limit + step)}>Show {Math.min(step, left).toLocaleString()} more</Button>
                <span className="secondary small">
                    {visible.length.toLocaleString()} of {items.length.toLocaleString()} shown. Search or filter to find the rest.
                </span>
            </div>
        ) : null;
    return { visible, more };
}

/** For lists rendered where hooks can't be called, e.g. inside a `Load` callback. */
export function Limited<T>({ items, step = 200, children }: { items: T[]; step?: number; children: (item: T) => ReactNode }) {
    const { visible, more } = useLimited(items, step);
    return (
        <>
            {visible.map(children)}
            {more}
        </>
    );
}
