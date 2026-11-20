import type { ReactNode } from "react";
import { IconButton } from "./Button";
import "./Notice.css";

/** A one-line message on a status color; dismissible when given onDismiss. */
export function Notice({ tone, children, onDismiss }: { tone: "success" | "danger" | "warning"; children: ReactNode; onDismiss?: () => void }) {
    return (
        <div className={`notice tone-${tone}`} role={tone === "danger" ? "alert" : "status"}>
            <span className="notice-text">{children}</span>
            {onDismiss && <IconButton icon="x" label="Dismiss" className="notice-dismiss" onClick={onDismiss} />}
        </div>
    );
}
