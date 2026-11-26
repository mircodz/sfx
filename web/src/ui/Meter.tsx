import { metricName, number } from "../lib/format";
import type { Metric } from "../lib/types";
import { Fields } from "./Fields";
import { Empty } from "./Section";
import "./Meter.css";

/** `relative` is a share of the largest, not of a capacity: grey, so it doesn't read as how full something is. */
export function ProgressBar({ pct, label, warnAbove, relative }: { pct: number; label: string; warnAbove?: number; relative?: boolean }) {
    const warn = warnAbove !== undefined && pct > warnAbove;
    return (
        <div className="track" role="progressbar" aria-label={label} aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100}>
            <div className={`fill ${warn ? "warning" : relative ? "relative" : ""}`} style={{ width: `${Math.min(pct, 100)}%` }} />
        </div>
    );
}

/** Metrics with a capacity as meters, the others as plain facts. */
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
                            <span>{metricName(m.name)}</span>
                            <span>
                                {number(m.value)} of {number(m.capacity!)} · {pct}%
                            </span>
                        </div>
                        <ProgressBar pct={pct} label={`${m.name}, ${pct}% of capacity`} warnAbove={85} />
                    </div>
                );
            })}
            {plain.length > 0 && <Fields fields={plain.map((m) => ({ name: metricName(m.name), value: number(m.value) }))} />}
        </div>
    );
}
