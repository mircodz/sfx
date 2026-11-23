import { api } from "../lib/api";
import { natural, plural } from "../lib/format";
import { useResource } from "../lib/resource";
import { href } from "../lib/router";
import type { ClusterReplica } from "../lib/types";
import { Load } from "../ui/Load";
import { Empty, Section } from "../ui/Section";
import "./Placement.css";

/** P primary, S active secondary, s idle secondary (still building), I instance, ? anything else. */
const letter = (role: string) => (role === "Primary" ? "P" : role === "Instance" ? "I" : role === "ActiveSecondary" ? "S" : role === "IdleSecondary" ? "s" : "?");

/**
 * Where a service's replicas are: partitions as rows, nodes as columns, each cell the replica's role. Shows
 * imbalance, partitions short of replicas, and what a node going down would cost.
 */
export function Placement({ appId, serviceId }: { appId: string; serviceId: string }) {
    const replicas = useResource(`service-replicas:${serviceId}`, () => api.serviceReplicas(appId, serviceId));
    return (
        <Load resource={replicas} shape="rows">
            {(all) => {
                const mine = all;
                if (mine.length === 0) return <Empty>This service has no replicas placed.</Empty>;
                // Only nodes that host this service: on a 400-node cluster, a column for every node says nothing.
                const nodes = [...new Set(mine.map((r) => r.node))].sort(natural);
                const partitions = [...new Set(mine.map((r) => r.partitionId))];
                const firstOf = new Map(mine.map((r) => [r.partitionId, r]));
                const at = new Map<string, ClusterReplica[]>();
                for (const r of mine) at.set(`${r.partitionId}|${r.node}`, [...(at.get(`${r.partitionId}|${r.node}`) ?? []), r]);
                return (
                    <Section label="Placement" aside={`${plural(partitions.length, "partition")} on ${plural(new Set(mine.map((r) => r.node)).size, "node")}`}>
                        <div className="placement-scroll">
                            <table className="placement">
                                <thead>
                                    <tr>
                                        <th scope="col">Partition</th>
                                        {nodes.map((n) => (
                                            <th key={n} scope="col">
                                                <a className="link" href={href.node(n)}>
                                                    {n}
                                                </a>
                                            </th>
                                        ))}
                                    </tr>
                                </thead>
                                <tbody>
                                    {partitions.map((p) => {
                                        const first = firstOf.get(p)!;
                                        return (
                                            <tr key={p}>
                                                <th scope="row">
                                                    <a className="link mono" href={href.partition(first.applicationId, first.serviceId, p)}>
                                                        {p.slice(0, 8)}
                                                    </a>
                                                </th>
                                                {nodes.map((n) => {
                                                    const here = at.get(`${p}|${n}`) ?? [];
                                                    return (
                                                        <td key={n}>
                                                            {here.map((r) => (
                                                                <span
                                                                    key={r.replicaId}
                                                                    className={`placement-role ${r.role === "Primary" ? "tone-accent" : r.status === "Down" || r.health === "Error" ? "tone-danger" : "tone-neutral"}`}
                                                                    title={`${r.role} · ${r.status} · ${r.health}`}
                                                                >
                                                                    {letter(r.role)}
                                                                    {r.status === "Down" ? " down" : r.health === "Error" ? " error" : ""}
                                                                </span>
                                                            ))}
                                                        </td>
                                                    );
                                                })}
                                            </tr>
                                        );
                                    })}
                                </tbody>
                            </table>
                        </div>
                        <p className="placement-legend secondary small">P primary · S secondary · s idle secondary (still building) · I instance · "down" or "error" in red when a replica is · only the nodes hosting this service.</p>
                    </Section>
                );
            }}
        </Load>
    );
}
