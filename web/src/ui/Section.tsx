import type { ReactNode } from "react";
import "./Section.css";

/** A labelled block of a page, with an optional aside on the right (a count, a link, a toggle). Sections don't nest. */
export function Section({ label, aside, children }: { label: ReactNode; aside?: ReactNode; children: ReactNode }) {
    return (
        <section className="section">
            <h2 className="section-label">
                <span>{label}</span>
                {aside && <span className="section-aside">{aside}</span>}
            </h2>
            {children}
        </section>
    );
}

/** A group's label inside a list (a node type, an application), with its count on the right. */
export function GroupHeading({ label, aside }: { label: ReactNode; aside?: ReactNode }) {
    return (
        <h3 className="group-heading">
            <span>{label}</span>
            {aside && <span>{aside}</span>}
        </h3>
    );
}

/** A short sentence for an empty list or a missing feature. */
export function Empty({ children }: { children: ReactNode }) {
    return <p className="empty">{children}</p>;
}
