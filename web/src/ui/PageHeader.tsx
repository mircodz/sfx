import type { ReactNode } from "react";
import { clock } from "../lib/format";
import type { Status } from "../lib/types";
import { IconButton } from "./Button";
import { Icon } from "./Icon";
import { Pill } from "./Pill";
import "./PageHeader.css";

export type Crumb = { label: string; href: string };

/**
 * The top of every page: where it is (breadcrumb, detail pages only), what it is (title and status), a one-line
 * summary, and on the right the page's actions and a refresh button.
 */
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
    /** When the data was loaded; shown on the refresh button rather than in the summary. */
    updated?: Date;
    actions?: ReactNode;
}) {
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
            {(actions || onRefresh) && (
                <div className="page-header-end">
                    {actions}
                    {onRefresh && <IconButton icon="refresh" label={updated ? `Refresh (updated ${clock(updated)})` : "Refresh"} onClick={onRefresh} />}
                </div>
            )}
        </header>
    );
}
