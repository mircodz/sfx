import type { ReactNode } from "react";
import "./Card.css";

/**
 * A dashboard tile: a title, an optional aside (a count, a link), then its content. `span` is its width in a
 * 12-column dashboard grid on wide screens; on narrow ones every card takes the full width.
 */
export function Card({ title, aside, span = 6, children }: { title: ReactNode; aside?: ReactNode; span?: 3 | 4 | 5 | 6 | 7 | 8 | 12; children: ReactNode }) {
    return (
        <section className={`card span-${span}`}>
            <header className="card-header">
                <h2 className="card-title">{title}</h2>
                {aside && <span className="card-aside">{aside}</span>}
            </header>
            <div className="card-body">{children}</div>
        </section>
    );
}

export function Dashboard({ children }: { children: ReactNode }) {
    return <div className="dashboard">{children}</div>;
}
