import { useEffect, useRef, useState } from "react";
import type { KeyboardEvent, PointerEvent, ReactNode } from "react";
import { IconButton } from "./Button";
import "./Overlay.css";

/** Focus stays inside; Esc and the backdrop close it. `onCancel` lets the owner veto closing (e.g. unsaved edits). */
export function useModal(onClose: () => void, onCancel?: () => boolean) {
    const ref = useRef<HTMLDialogElement>(null);
    useEffect(() => {
        ref.current?.showModal();
        // The page behind doesn't scroll while a modal is open; the panel body is the one place that does.
        const body = document.body.style.overflow;
        document.body.style.overflow = "hidden";
        return () => {
            document.body.style.overflow = body;
        };
    }, []);
    const tryClose = () => {
        if (!onCancel || onCancel()) ref.current?.close();
    };
    const props = {
        ref,
        onClose,
        // Not via the dialog's cancel event: browsers stop letting us veto it after the first time, so a second Escape
        // would close it with the edits.
        onKeyDown: (e: React.KeyboardEvent) => {
            if (e.key === "Escape" && !e.defaultPrevented) {
                e.preventDefault();
                tryClose();
            }
        },
        onCancel: (e: React.SyntheticEvent) => e.preventDefault(),
        // Only a click outside the dialog's box closes it, not one on its own padding.
        onMouseDown: (e: React.MouseEvent) => {
            const box = ref.current?.getBoundingClientRect();
            if (e.target === ref.current && box && (e.clientX < box.left || e.clientX > box.right || e.clientY < box.top || e.clientY > box.bottom)) tryClose();
        },
    };
    return { props, close: tryClose };
}

export function Dialog({
    title,
    onClose,
    onCancel,
    children,
    footer,
}: {
    title: ReactNode;
    onClose: () => void;
    /** Return false to keep it open, e.g. while the action runs. */
    onCancel?: () => boolean;
    children: ReactNode;
    footer: ReactNode;
}) {
    const { props } = useModal(onClose, onCancel);
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

/** For changes with a form. Its body is the one place that scrolls on its own. */
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
    /** Gets the close that asks first (like Escape and the close button), for a Cancel button. */
    footer: ReactNode | ((close: () => void) => ReactNode);
}) {
    const { props, close } = useModal(onClose, onCancel);
    const [width, resize] = usePanelWidth();
    return (
        <dialog {...props} className="side-panel" aria-labelledby="panel-title" style={{ width }}>
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
            <div className="side-panel-footer">{typeof footer === "function" ? footer(close) : footer}</div>
            {/* After the content, so focus starts in the form rather than on the handle. */}
            <ResizeHandle width={width} onResize={resize} />
        </dialog>
    );
}

const widthKey = "sfx-panel-width";
const defaultWidth = 520;
const minWidth = 380;
const maxWidth = () => Math.max(minWidth, Math.round(window.innerWidth * 0.9));
const clamp = (w: number) => Math.min(maxWidth(), Math.max(minWidth, Math.round(w)));

/** Remembered across panels and reloads. */
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

/** Drag, or focus and use the arrow keys (Shift for bigger steps); Home or a double-click restores the default. */
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
