import { useRef, useState } from "react";
import type { KeyboardEvent, PointerEvent, ReactNode } from "react";
import "./Split.css";

const widthKey = "sfx-aside-width";
const minWidth = 280;
/** The main column keeps at least this share of the page, whatever the aside is dragged to. */
const mainShare = 0.35;

/** The aside is about 40% wide until dragged (remembered across pages and reloads); narrow screens put it first. */
export function Split({ aside, children }: { aside: ReactNode; children: ReactNode }) {
    const split = useRef<HTMLDivElement>(null);
    const asideRef = useRef<HTMLElement>(null);
    const [width, setWidth] = useState<number | null>(() => Number(localStorage.getItem(widthKey)) || null);
    // The width just set, ahead of the render that applies it, so held-down arrow keys keep counting from it.
    const latest = useRef<number | null>(width);

    const resize = (next: number | null) => {
        const total = split.current?.getBoundingClientRect().width ?? 0;
        const w = next === null || !total ? null : Math.round(Math.min(total * (1 - mainShare), Math.max(minWidth, next)));
        latest.current = w;
        setWidth(w);
        if (w === null) localStorage.removeItem(widthKey);
        else localStorage.setItem(widthKey, String(w));
    };

    return (
        <div className="split-wrap">
            <div ref={split} className="split" style={width ? ({ "--aside-width": `${width}px` } as React.CSSProperties) : undefined}>
                <aside ref={asideRef} className="split-aside">
                    <Handle
                        current={() => latest.current ?? asideRef.current?.getBoundingClientRect().width ?? minWidth}
                        edge={() => split.current?.getBoundingClientRect().right ?? 0}
                        onResize={resize}
                    />
                    {aside}
                </aside>
                <div className="split-main">{children}</div>
            </div>
        </div>
    );
}

/** Drag, or focus and use the arrow keys (Shift for bigger steps); Home or a double-click restores the default. */
function Handle({ current, edge, onResize }: { current: () => number; edge: () => number; onResize: (width: number | null) => void }) {
    const onPointerDown = (e: PointerEvent<HTMLDivElement>) => {
        e.preventDefault();
        e.currentTarget.setPointerCapture(e.pointerId);
    };
    const onPointerMove = (e: PointerEvent<HTMLDivElement>) => {
        if (e.currentTarget.hasPointerCapture(e.pointerId)) onResize(edge() - e.clientX);
    };
    const onKeyDown = (e: KeyboardEvent) => {
        const step = e.shiftKey ? 96 : 24;
        if (e.key === "ArrowLeft") onResize(current() + step);
        else if (e.key === "ArrowRight") onResize(current() - step);
        else if (e.key === "Home") onResize(null);
        else return;
        e.preventDefault();
    };
    return (
        <div
            className="split-resize"
            role="separator"
            aria-orientation="vertical"
            aria-label="Resize the facts column"
            tabIndex={0}
            onPointerDown={onPointerDown}
            onPointerMove={onPointerMove}
            onKeyDown={onKeyDown}
            onDoubleClick={() => onResize(null)}
        />
    );
}
