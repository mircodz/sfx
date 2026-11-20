import { api } from "../lib/api";
import { plural } from "../lib/format";
import { useResource } from "../lib/resource";
import { href } from "../lib/router";
import type { ClusterReplica } from "../lib/types";
import { Load } from "../ui/Load";
import { Empty, Section } from "../ui/Section";
import "./Placement.css";

const letter = (role: string) => (role === "Primary" ? "P" : role === "Instance" ? "I" : role.endsWith("Secondary") ? "S" : role.slice(0, 1));

/**
 * Where a service's replicas are: partitions as rows, nodes as columns, each cell the replica's role. Shows
 * imbalance, partitions short of replicas, and what a node going down would cost.
 */
export function Placement({ serviceId }: { serviceId: string }) {
    const replicas = useResource("replicas", api.replicas);
    return (
        <Load resource={replicas} shape="rows">
            {(all) => {
                const mine = all.filter((r) => r.serviceId === serviceId);
                if (mine.length === 0) return <Empty>This service has no replicas placed.</Empty>;
                const nodes = [...new Set(all.map((r) => r.node))].sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
                const partitions = [...new Set(mine.map((r) => r.partitionId))];
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
                                        const first = mine.find((r) => r.partitionId === p)!;
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
                        <p className="placement-legend secondary small">P primary · S secondary · I instance · red when down or in error · empty: none on that node</p>
                    </Section>
                );
            }}
        </Load>
    );
}
