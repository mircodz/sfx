import { number } from "../lib/format";
import type { Metric } from "../lib/types";
import { Fields } from "./Fields";
import { Empty } from "./Section";
import "./Meter.css";

/** A 0-100 bar; past `warnAbove` it turns amber. */
export function ProgressBar({ pct, label, warnAbove }: { pct: number; label: string; warnAbove?: number }) {
    const warn = warnAbove !== undefined && pct > warnAbove;
    return (
        <div className="track" role="progressbar" aria-label={label} aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100}>
            <div className={`fill ${warn ? "warning" : ""}`} style={{ width: `${Math.min(pct, 100)}%` }} />
        </div>
    );
}

/** Load: metrics with a capacity as meters, the others as plain facts. */
export function Meters({ metrics }: { metrics: Metric[] }) {
    if (metrics.length === 0) return <Empty>No load reported.</Empty>;
    const capped = metrics.filter((m) => m.capacity);
    const plain = metrics.filter((m) => !m.capacity);
    return (
        <div className="meters">
            {capped.map((m) => {
                const pct = Math.round((m.value / m.capacity!) * 100);
                return (
                    <div key={m.name}>
                        <div className="meter-head">
                            <span>{m.name}</span>
                            <span>
                                {number(m.value)} of {number(m.capacity!)} · {pct}%
                            </span>
                        </div>
                        <ProgressBar pct={pct} label={`${m.name}, ${pct}% of capacity`} warnAbove={85} />
                    </div>
                );
            })}
            {plain.length > 0 && <Fields fields={plain.map((m) => ({ name: m.name, value: number(m.value) }))} />}
        </div>
    );
}
