import { plural } from "../lib/format";
import type { Health } from "../lib/types";
import "./HealthBar.css";

const order: Health[] = ["Error", "Warning", "Unknown", "Healthy"];
const tone: Record<Health, string> = { Error: "danger", Warning: "warning", Unknown: "neutral", Healthy: "success" };

/**
 * How many of something are in each health state, as one stacked bar and the counts in words. Takes either the
 * states themselves or counts per state (what the cluster's health statistics give, however many there are).
 */
export function HealthBar({ label, healths, counts: given, href }: { label: string; healths?: Health[]; counts?: Partial<Record<Health, number>>; href?: string }) {
    const tally: Partial<Record<Health, number>> = given ?? {};
    if (!given) for (const h of healths ?? []) tally[h] = (tally[h] ?? 0) + 1;
    const counts = order.map((h) => ({ h, n: tally[h] ?? 0 })).filter((c) => c.n > 0);
    const total = counts.reduce((sum, c) => sum + c.n, 0);
    // Neither given: still loading, which isn't the same as none.
    const loading = given === undefined && healths === undefined;
    const name = href ? (
        <a className="link" href={href}>
            {label}
        </a>
    ) : (
        label
    );
    return (
        <div className="health-bar">
            <div className="health-bar-head">
                <span className="strong">{name}</span>
                <span className="secondary">
                    {loading ? "…" : total === 0 ? "None" : counts.map((c) => `${c.n.toLocaleString()} ${c.h.toLowerCase()}`).join(" · ")}
                </span>
            </div>
            <div className="health-bar-track" role="img" aria-label={`${label}: ${plural(total, "in total", "in total")}, ${counts.map((c) => `${c.n} ${c.h}`).join(", ")}`}>
                {counts.map((c) => (
                    <span key={c.h} className={`health-bar-part tone-${tone[c.h]}`} style={{ flexGrow: c.n }} />
                ))}
            </div>
        </div>
    );
}
