import { toneOf } from "../lib/health";
import { domainState } from "../lib/upgrades";
import type { Upgrade } from "../lib/types";
import "./DomainStrip.css";

/** An upgrade's domains in a row, colored by state, the current one outlined; then its nodes and what they wait on. */
export function DomainStrip({ upgrade: u }: { upgrade: Upgrade }) {
    return (
        <>
            <ol className="domains" aria-label="Upgrade domains">
                {u.domains.map((d) => {
                    const state = domainState(u, d);
                    return (
                        <li
                            key={d.name}
                            className={`domain tone-${toneOf(state)} ${d.name === u.currentDomain ? "current" : ""}`}
                            aria-label={`Upgrade domain ${d.name}: ${state}`}
                        >
                            <span className="domain-name">UD {d.name}</span>
                            <span className="domain-state">{state}</span>
                        </li>
                    );
                })}
            </ol>
            {u.nodes.length > 0 && (
                <ul className="domain-nodes">
                    {u.nodes.map((n) => (
                        <li key={n.node}>
                            <span className="strong">{n.node}</span>
                            <span className="secondary"> · {n.phase}</span>
                            {n.pendingSafetyChecks.length > 0 && <span className="text-warning"> · waiting on {n.pendingSafetyChecks.join(", ")}</span>}
                        </li>
                    ))}
                </ul>
            )}
        </>
    );
}
