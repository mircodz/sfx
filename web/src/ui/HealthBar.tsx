import { plural } from "../lib/format";
import type { Health } from "../lib/types";
import "./HealthBar.css";

const order: Health[] = ["Error", "Warning", "Unknown", "Healthy"];
const tone: Record<Health, string> = { Error: "danger", Warning: "warning", Unknown: "neutral", Healthy: "success" };

/** How many of something are in each health state, as one stacked bar and the counts in words. */
export function HealthBar({ label, healths, href }: { label: string; healths: Health[]; href?: string }) {
    const counts = order.map((h) => ({ h, n: healths.filter((x) => x === h).length })).filter((c) => c.n > 0);
    const total = healths.length;
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
                    {total === 0 ? "None" : counts.map((c) => `${c.n.toLocaleString()} ${c.h.toLowerCase()}`).join(" · ")}
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
