import type { ReactNode } from "react";
import { Button, IconButton } from "./Button";
import "./Notice.css";

/** A one-line message on a status color; dismissible when given onDismiss, retryable when given onRetry. */
export function Notice({ tone, children, onDismiss, onRetry }: { tone: "success" | "danger" | "warning"; children: ReactNode; onDismiss?: () => void; onRetry?: () => void }) {
    return (
        <div className={`notice tone-${tone}`} role={tone === "danger" ? "alert" : "status"}>
            <span className="notice-text">{children}</span>
            {onRetry && <Button onClick={onRetry}>Try again</Button>}
            {onDismiss && <IconButton icon="x" label="Dismiss" className="notice-dismiss" onClick={onDismiss} />}
        </div>
    );
}
