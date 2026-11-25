import type { ReactNode } from "react";
import { useOfferRefresh } from "../lib/pageRefresh";
import type { Status } from "../lib/types";
import { Icon } from "./Icon";
import { Pill } from "./Pill";
import "./PageHeader.css";

export type Crumb = { label: string; href: string };

/** The breadcrumb is for detail pages only. The refresh goes to the top bar, with when it was last loaded. */
export function PageHeader({
    title,
    summary,
    crumbs,
    status,
    onRefresh,
    updated,
    actions,
}: {
    title: ReactNode;
    summary: ReactNode;
    crumbs?: Crumb[];
    status?: Status;
    onRefresh?: () => void;
    /** Shown next to the refresh button in the top bar. */
    updated?: Date;
    actions?: ReactNode;
}) {
    useOfferRefresh(onRefresh, updated);
    return (
        <header className="page-header">
            <div className="page-header-main">
                {crumbs && crumbs.length > 0 && (
                    <nav aria-label="Breadcrumb">
                        <ol className="crumbs">
                            {crumbs.map((c) => (
                                <li key={c.href}>
                                    <a href={c.href} title={c.label}>
                                        {c.label}
                                    </a>
                                    <Icon name="chevron-right" className="crumb-sep" />
                                </li>
                            ))}
                        </ol>
                    </nav>
                )}
                <div className="page-title-row">
                    <h1 className="page-title">{title}</h1>
                    {status && <Pill status={status} />}
                </div>
                <div className="page-summary">{summary}</div>
            </div>
            {actions && <div className="page-header-end">{actions}</div>}
        </header>
    );
}
