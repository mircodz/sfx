import { useState } from "react";
import { ago, plural } from "../lib/format";
import type { Evaluation, HealthReport } from "../lib/types";
import { Disclosure } from "./Disclosure";
import { Icon } from "./Icon";
import { Pill } from "./Pill";
import { Empty, Section } from "./Section";
import { TimelineRow } from "./Timeline";
import "./Health.css";

/** The chain of health evaluations behind a state, children indented under their parent. */
export function Evaluations({ evaluations }: { evaluations: Evaluation[] }) {
    return (
        <div className="evaluations">
            {evaluations.map((e, i) => (
                <div key={i} className="evaluation">
                    {Array.from({ length: e.depth }, (_, d) => (
                        <span key={d} className="evaluation-indent" aria-hidden="true" />
                    ))}
                    <span className="evaluation-status">
                        <Pill status={e.health} />
                    </span>
                    <span className="evaluation-text">{e.description}</span>
                </div>
            ))}
        </div>
    );
}

/**
 * Errors and warnings, collapsed behind their counts; with `href`, a link to where they're shown in full (the Health
 * tab) instead. Renders nothing when there are none.
 */
export function ErrorsAndWarnings({ evaluations, href }: { evaluations: Evaluation[]; href?: string }) {
    if (evaluations.length === 0) return null;
    const errors = evaluations.filter((e) => e.health === "Error").length;
    const warnings = evaluations.filter((e) => e.health === "Warning").length;
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

/** The Health tab: errors and warnings expanded, then the health events, unhealthy ones first. */
export function HealthPanel({ health }: { health: HealthReport }) {
    const [all, setAll] = useState(false);
    const unhealthy = health.events.filter((e) => e.health !== "Healthy");
    const events = all ? health.events : unhealthy;
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
                {events.length === 0 && <Empty>No unhealthy events.</Empty>}
                {events.map((e, i) => (
                    <TimelineRow
                        key={i}
                        title={`${e.source} · ${e.property}`}
                        sub={[e.modified ? `Reported ${ago(e.modified)}` : null, e.expired ? "expired" : null].filter(Boolean).join(" · ")}
                        detail={e.description}
                        status={e.health}
                    />
                ))}
            </Section>
        </>
    );
}
