import { api } from "../lib/api";
import { number, plural } from "../lib/format";
import { useResource } from "../lib/resource";
import { href } from "../lib/router";
import { Link } from "../ui/Button";
import { Load } from "../ui/Load";
import { ProgressBar } from "../ui/Meter";
import { Empty, Section } from "../ui/Section";

/** Load per metric: the cluster's total against its capacity, then each node, busiest first. */
export function LoadView() {
    const load = useResource("load", api.load);
    return (
        <Load resource={load} shape="rows">
            {(d) =>
                d.metrics.length === 0 ? (
                    <Empty>No load reported.</Empty>
                ) : (
                    <>
                        {d.metrics.map((m) => {
                            const nodes = [...m.nodes].sort((a, b) => (b.capacity ? b.load / b.capacity : b.load) - (a.capacity ? a.load / a.capacity : a.load));
                            const max = Math.max(1, ...m.nodes.map((n) => n.capacity ?? n.load));
                            const pct = m.clusterCapacity ? Math.round((m.clusterLoad / m.clusterCapacity) * 100) : undefined;
                            return (
                                <Section
                                    key={m.name}
                                    label={m.name}
                                    aside={`${number(m.clusterLoad)}${m.clusterCapacity ? ` of ${number(m.clusterCapacity)} · ${pct}%` : ""}${m.balanced === false ? " · not balanced" : ""} · ${plural(m.nodes.length, "node")}`}
                                >
                                    <div className="load-grid">
                                        {nodes.map((n) => {
                                            const p = Math.round((n.load / (n.capacity ?? max)) * 100);
                                            return (
                                                <div key={n.node} className="load-row">
                                                    <Link href={href.node(n.node)}>{n.node}</Link>
                                                    <ProgressBar pct={p} label={`${n.node}: ${number(n.load)}`} warnAbove={n.capacity ? 85 : undefined} />
                                                    <span className="secondary small">
                                                        {number(n.load)}
                                                        {n.capacity ? ` of ${number(n.capacity)}` : ""}
                                                    </span>
                                                </div>
                                            );
                                        })}
                                    </div>
                                </Section>
                            );
                        })}
                    </>
                )
            }
        </Load>
    );
}
