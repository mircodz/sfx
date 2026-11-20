import type { ReactNode } from "react";
import { Icon } from "./Icon";
import "./Card.css";

/**
 * A dashboard tile: a title, an optional aside (a count, a link), then its content. `span` is its width in a
 * 12-column dashboard grid on wide screens; on narrow ones every card takes the full width.
 */
export function Card({
    title,
    href,
    aside,
    span = 6,
    children,
}: {
    title: ReactNode;
    /** Where the rest is; the title links there, since that's where people click first. */
    href?: string;
    aside?: ReactNode;
    span?: 3 | 4 | 5 | 6 | 7 | 8 | 12;
    children: ReactNode;
}) {
    return (
        <section className={`card span-${span}`}>
            <header className="card-header">
                <h2 className="card-title">
                    {href ? (
                        <a className="card-title-link" href={href}>
                            {title}
                            <Icon name="chevron-right" className="link-icon" />
                        </a>
                    ) : (
                        title
                    )}
                </h2>
                {aside && <span className="card-aside">{aside}</span>}
            </header>
            <div className="card-body">{children}</div>
        </section>
    );
}

export function Dashboard({ children }: { children: ReactNode }) {
    return <div className="dashboard">{children}</div>;
}
