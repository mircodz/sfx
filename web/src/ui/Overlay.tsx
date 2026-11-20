import { useEffect, useRef, useState } from "react";
import type { KeyboardEvent, PointerEvent, ReactNode } from "react";
import { IconButton } from "./Button";
import "./Overlay.css";

/**
 * A modal <dialog>: focus stays inside, Esc and the backdrop close it. `onCancel` lets the owner veto closing
 * (e.g. to ask about unsaved edits); without it, closing just closes.
 */
function useModal(onClose: () => void, onCancel?: () => boolean) {
    const ref = useRef<HTMLDialogElement>(null);
    useEffect(() => {
        ref.current?.showModal();
    }, []);
    const tryClose = () => {
        if (!onCancel || onCancel()) ref.current?.close();
    };
    const props = {
        ref,
        onClose,
        onCancel: (e: React.SyntheticEvent) => {
            e.preventDefault();
            tryClose();
        },
        onMouseDown: (e: React.MouseEvent) => e.target === ref.current && tryClose(),
    };
    return { props, close: tryClose };
}

/** A small centered dialog for a confirmation. */
export function Dialog({ title, onClose, children, footer }: { title: ReactNode; onClose: () => void; children: ReactNode; footer: ReactNode }) {
    const { props } = useModal(onClose);
    return (
        <dialog {...props} className="dialog" aria-labelledby="dialog-title">
            <h2 id="dialog-title" className="dialog-title">
                {title}
            </h2>
            <div className="dialog-body">{children}</div>
            <div className="dialog-footer">{footer}</div>
        </dialog>
    );
}

/** A panel that slides in from the right, for changes with a form. Its body is the one place that scrolls on its own. */
export function SidePanel({
    title,
    subtitle,
    onClose,
    onCancel,
    children,
    footer,
}: {
    title: ReactNode;
    subtitle?: ReactNode;
    onClose: () => void;
    onCancel?: () => boolean;
    children: ReactNode;
    footer: ReactNode;
}) {
    const { props, close } = useModal(onClose, onCancel);
    const [width, resize] = usePanelWidth();
    return (
        <dialog {...props} className="side-panel" aria-labelledby="panel-title" style={{ width }}>
            <ResizeHandle width={width} onResize={resize} />
            <div className="side-panel-header">
                <div className="side-panel-heading">
                    <h2 id="panel-title" className="dialog-title">
                        {title}
                    </h2>
                    {subtitle}
                </div>
                <IconButton icon="x" label="Close" onClick={close} />
            </div>
            <div className="side-panel-body">{children}</div>
            <div className="side-panel-footer">{footer}</div>
        </dialog>
    );
}

const widthKey = "sfx-panel-width";
const defaultWidth = 520;
const minWidth = 380;
const maxWidth = () => Math.max(minWidth, Math.round(window.innerWidth * 0.9));
const clamp = (w: number) => Math.min(maxWidth(), Math.max(minWidth, Math.round(w)));

/** The side panel's width, remembered across panels and reloads. */
function usePanelWidth(): [number, (width: number) => void] {
    const [width, setWidth] = useState(() => clamp(Number(localStorage.getItem(widthKey)) || defaultWidth));
    const resize = (next: number) => {
        const w = clamp(next);
        setWidth(w);
        if (w === defaultWidth) localStorage.removeItem(widthKey);
        else localStorage.setItem(widthKey, String(w));
    };
    return [width, resize];
}

/**
 * The panel's left edge: drag it, or focus it and use the arrow keys (Shift for bigger steps); Home or a
 * double-click goes back to the default width.
 */
function ResizeHandle({ width, onResize }: { width: number; onResize: (width: number) => void }) {
    const onPointerDown = (e: PointerEvent<HTMLDivElement>) => {
        e.preventDefault();
        e.currentTarget.setPointerCapture(e.pointerId);
    };
    const onPointerMove = (e: PointerEvent<HTMLDivElement>) => {
        if (e.currentTarget.hasPointerCapture(e.pointerId)) onResize(window.innerWidth - e.clientX);
    };
    const onKeyDown = (e: KeyboardEvent) => {
        const step = e.shiftKey ? 96 : 24;
        if (e.key === "ArrowLeft") onResize(width + step);
        else if (e.key === "ArrowRight") onResize(width - step);
        else if (e.key === "Home") onResize(defaultWidth);
        else return;
        e.preventDefault();
    };
    return (
        <div
            className="side-panel-resize"
            role="separator"
            aria-orientation="vertical"
            aria-label="Resize the panel"
            aria-valuenow={width}
            aria-valuemin={minWidth}
            aria-valuemax={maxWidth()}
            tabIndex={0}
            onPointerDown={onPointerDown}
            onPointerMove={onPointerMove}
            onKeyDown={onKeyDown}
            onDoubleClick={() => onResize(defaultWidth)}
        />
    );
}
