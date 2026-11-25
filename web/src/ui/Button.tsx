import type { ButtonHTMLAttributes, ReactNode } from "react";
import { Icon } from "./Icon";
import "./Button.css";

type Variant = "default" | "primary" | "danger" | "danger-filled";

/** At most one primary per view: the step that moves a flow forward. */
export function Button({ variant = "default", className = "", ...rest }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant }) {
    return <button type="button" className={`btn btn-${variant} ${className}`} {...rest} />;
}

/** The label is required: the icon says nothing to a screen reader. */
export function IconButton({
    icon,
    label,
    bordered,
    className = "",
    ...rest
}: Omit<ButtonHTMLAttributes<HTMLButtonElement>, "children"> & { icon: string; label: string; bordered?: boolean }) {
    return (
        <button type="button" className={`icon-button ${bordered ? "bordered" : ""} ${className}`} aria-label={label} title={label} {...rest}>
            <Icon name={icon} />
        </button>
    );
}

/** Reads like the text around it; the chevron says it goes somewhere. */
export function Link({ href, children, className = "" }: { href: string; children: ReactNode; className?: string }) {
    return (
        <a className={`link ${className}`} href={href}>
            {children}
            <Icon name="chevron-right" className="link-icon" />
        </a>
    );
}
