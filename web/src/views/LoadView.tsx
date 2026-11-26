import { api } from "../lib/api";
import { metricName, number, plural } from "../lib/format";
import { useResource } from "../lib/resource";
import { href } from "../lib/router";
import { Link } from "../ui/Button";
import { Load } from "../ui/Load";
import { ProgressBar } from "../ui/Meter";
import { Empty, Section } from "../ui/Section";
import { Limited } from "../ui/ShowMore";

/** Per metric: the cluster's total against its capacity, then each node. */
export function LoadView() {
    const load = useResource("load:nodes", () => api.load(true));
    return (
        <Load resource={load} shape="rows">
            {(d) =>
                d.metrics.length === 0 ? (
                    <Empty>No load reported.</Empty>
                ) : (
                    <>
                        {d.metrics.filter(reported).map((m) => {
                            // Busiest first, by share of capacity where there is one, else by load.
                            const share = (n: { load: number; capacity?: number }) => (n.capacity ? n.load / n.capacity : m.clusterCapacity ? n.load / m.clusterCapacity : n.load);
                            const nodes = [...m.nodes].sort((a, b) => share(b) - share(a));
                            const max = Math.max(1, ...m.nodes.map((n) => n.capacity ?? n.load));
                            const pct = m.clusterCapacity ? Math.round((m.clusterLoad / m.clusterCapacity) * 100) : undefined;
                            return (
                                <Section
                                    key={m.name}
                                    label={metricName(m.name)}
                                    aside={`${number(m.clusterLoad)}${m.clusterCapacity ? ` of ${number(m.clusterCapacity)} · ${pct}%` : ""}${m.balanced === false ? " · not balanced" : ""} · ${plural(m.nodes.length, "node")}`}
                                >
                                    <div className="load-grid">
                                        <Limited items={nodes} step={10}>
                                        {(n) => {
                                            const p = Math.round((n.load / (n.capacity ?? max)) * 100);
                                            return (
                                                <div key={n.node} className="load-row">
                                                    <Link href={href.node(n.node)}>{n.node}</Link>
                                                    <ProgressBar pct={p} label={`${n.node}: ${number(n.load)}`} warnAbove={n.capacity ? 85 : undefined} relative={!n.capacity} />
                                                    <span className="secondary small">
                                                        {number(n.load)}
                                                        {n.capacity ? ` of ${number(n.capacity)}` : ""}
                                                    </span>
                                                </div>
                                            );
                                        }}
                                        </Limited>
                                    </div>
                                </Section>
                            );
                        })}
                        {d.metrics.some((m) => !reported(m)) && (
                            <Section label="Nothing reported">
                                <Empty>
                                    No node reports load for {d.metrics.filter((m) => !reported(m)).map((m) => metricName(m.name)).join(", ")}.
                                </Empty>
                            </Section>
                        )}
                    </>
                )
            }
        </Load>
    );
}

/** A metric every node reports 0 for is a list of zeros; it gets one line instead. */
const reported = (m: { clusterLoad: number; nodes: { load: number }[] }) => m.clusterLoad > 0 || m.nodes.some((n) => n.load > 0);
