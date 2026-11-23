import { useState } from "react";
import type { ReactNode } from "react";
import { ago, plural, when } from "../lib/format";
import type { Evaluation, HealthReport } from "../lib/types";
import { Disclosure } from "./Disclosure";
import { Icon } from "./Icon";
import { Pill } from "./Pill";
import { Empty, Section } from "./Section";
import { TimelineRow } from "./Timeline";
import "./Health.css";

/** Each evaluation's parent: the nearest one before it that's one level shallower (-1 at the top). */
function parentsOf(evaluations: Evaluation[]) {
    const last: number[] = [];
    return evaluations.map((e, i) => {
        const parent = e.depth === 0 ? -1 : (last[e.depth - 1] ?? -1);
        last[e.depth] = i;
        last.length = e.depth + 1;
        return parent;
    });
}

const siblingsShown = 5;

/**
 * The chain of health evaluations behind a state, children indented under their parent. Beyond 5 siblings under one
 * parent, the rest wait behind "Show N more": a cluster can list hundreds.
 */
export function Evaluations({ evaluations }: { evaluations: Evaluation[] }) {
    const [expanded, setExpanded] = useState<Set<number>>(new Set());
    const parents = parentsOf(evaluations);
    const total = new Map<number, number>();
    for (const p of parents) total.set(p, (total.get(p) ?? 0) + 1);

    const seen = new Map<number, number>();
    const hidden = new Set<number>();
    const rows: ReactNode[] = [];
    evaluations.forEach((e, i) => {
        const parent = parents[i];
        // Under a hidden evaluation, everything is hidden with it.
        if (hidden.has(parent)) {
            hidden.add(i);
            return;
        }
        const n = (seen.get(parent) ?? 0) + 1;
        seen.set(parent, n);
        const indent = Array.from({ length: e.depth }, (_, d) => <span key={d} className="evaluation-indent" aria-hidden="true" />);
        if (n <= siblingsShown || expanded.has(parent)) {
            rows.push(
                <div key={i} className="evaluation">
                    {indent}
                    <span className="evaluation-status">
                        <Pill status={e.health} />
                    </span>
                    <span className="evaluation-text">{e.description}</span>
                </div>,
            );
        } else {
            hidden.add(i);
        }
        if (n === siblingsShown + 1 && !expanded.has(parent)) {
            rows.push(
                <div key={i} className="evaluation">
                    {indent}
                    <button type="button" className="text-button" onClick={() => setExpanded((x) => new Set(x).add(parent))}>
                        Show {total.get(parent)! - siblingsShown} more
                    </button>
                </div>,
            );
        }
    });
    return <div className="evaluations">{rows}</div>;
}

/**
 * Errors and warnings, collapsed behind their counts; with `href`, a link to where they're shown in full (the Health
 * tab) instead. Renders nothing when there are none.
 */
export function ErrorsAndWarnings({ evaluations, href }: { evaluations: Evaluation[]; href?: string }) {
    if (evaluations.length === 0) return null;
    // Count problems, not every step of the chain explaining them: only evaluations nothing else hangs under.
    const leaves = evaluations.filter((e, i) => !(evaluations[i + 1]?.depth > e.depth));
    const errors = leaves.filter((e) => e.health === "Error").length;
    const warnings = leaves.filter((e) => e.health === "Warning").length;
    const counts = [errors ? plural(errors, "error") : null, warnings ? plural(warnings, "warning") : null].filter(Boolean).join(" · ");
    if (href) {
        return (
            <a className="errors-and-warnings errors-link" href={href}>
                <span>Errors and warnings</span>
                <span className="disclosure-aside">{counts}</span>
                <Icon name="chevron-right" className="link-icon" />
            </a>
        );
    }
    return (
        <Disclosure label="Errors and warnings" aside={counts} className="errors-and-warnings">
            <Evaluations evaluations={evaluations} />
        </Disclosure>
    );
}

const rank = (h: string) => ({ Error: 0, Warning: 1, Unknown: 2 } as Record<string, number>)[h] ?? 3;

/** The Health tab: errors and warnings expanded, then the health events, unhealthy ones first. */
export function HealthPanel({ health }: { health: HealthReport }) {
    const [all, setAll] = useState(false);
    // An expired report that isn't removed counts as an error, whatever state it was reported with.
    const events = health.events.map((e) => (e.expired ? { ...e, health: "Error" as const } : e)).sort((a, b) => rank(a.health) - rank(b.health));
    const unhealthy = events.filter((e) => e.health !== "Healthy");
    const shownEvents = all ? events : unhealthy;
    return (
        <>
            {health.evaluations.length > 0 ? (
                <Section label="Errors and warnings">
                    <Evaluations evaluations={health.evaluations} />
                </Section>
            ) : (
                <Section label="Errors and warnings">
                    <Empty>None: {health.health === "Healthy" ? "everything here is healthy." : "nothing is reported as unhealthy."}</Empty>
                </Section>
            )}
            <Section
                label="Health events"
                aside={
                    health.events.length > unhealthy.length && (
                        <button type="button" className="text-button" aria-pressed={all} onClick={() => setAll(!all)}>
                            {all ? "Show unhealthy only" : `Show all ${health.events.length}`}
                        </button>
                    )
                }
            >
                {shownEvents.length === 0 && <Empty>No unhealthy events.</Empty>}
                {shownEvents.map((e, i) => (
                    <TimelineRow
                        key={i}
                        time={e.modified ? <span title={`Reported ${ago(e.modified)}`}>{when(e.modified)}</span> : "–"}
                        title={`${e.source} · ${e.property}`}
                        sub={e.expired ? "Expired, so it counts as an error" : undefined}
                        detail={e.description}
                        status={e.health}
                    />
                ))}
            </Section>
        </>
    );
}
