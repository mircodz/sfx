import type { ReactNode } from "react";
import { Icon } from "./Icon";
import "./Card.css";

/** `span` is its width in a 12-column grid on wide screens; on narrow ones every card is full width. */
export function Card({
    title,
    href,
    aside,
    span = 6,
    children,
}: {
    title: ReactNode;
    /** The title links here, since that's where people click first. */
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
