import type { ButtonHTMLAttributes, ReactNode } from "react";
import { Icon } from "./Icon";
import "./Button.css";

type Variant = "default" | "primary" | "danger" | "danger-filled";

/** A text button. At most one primary per view, for the step that moves a flow forward. */
export function Button({ variant = "default", className = "", ...rest }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant }) {
    return <button type="button" className={`btn btn-${variant} ${className}`} {...rest} />;
}

/** An icon-only button; the label is required, since the icon says nothing to a screen reader. */
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

/** A link that reads like the text around it, with a chevron saying it goes somewhere. */
export function Link({ href, children, className = "" }: { href: string; children: ReactNode; className?: string }) {
    return (
        <a className={`link ${className}`} href={href}>
            {children}
            <Icon name="chevron-right" className="link-icon" />
        </a>
    );
}
