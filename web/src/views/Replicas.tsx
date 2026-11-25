import { useState } from "react";
import { api } from "../lib/api";
import { count, plural } from "../lib/format";
import { healthFilters, matches, worst } from "../lib/health";
import type { HealthFilter } from "../lib/health";
import { useResource } from "../lib/resource";
import { href, useQueryParam } from "../lib/router";
import type { ClusterReplica } from "../lib/types";
import { Button } from "../ui/Button";
import { Chips } from "../ui/Chips";
import { Load } from "../ui/Load";
import { MetricCard, Metrics } from "../ui/MetricCard";
import { PageHeader } from "../ui/PageHeader";
import { Pill } from "../ui/Pill";
import { LinkRow } from "../ui/Row";
import { Empty, GroupHeading, Section } from "../ui/Section";
import { SearchBox, Toolbar } from "../ui/Toolbar";
import { roleIcon } from "./PartitionDetail";

const views = ["Replicas", "Partitions"] as const;
type View = (typeof views)[number];
const roles = ["All", "Primary", "Secondary", "Instance"] as const;
type Role = (typeof roles)[number];
const groupings = ["None", "Node", "Application", "Role"] as const;
type Grouping = (typeof groupings)[number];

const pageSize = 200;

function roleMatches(role: Role, r: ClusterReplica) {
    if (role === "All") return true;
    if (role === "Secondary") return r.role.endsWith("Secondary");
    return r.role === role;
}

function groupKey(grouping: Grouping, r: ClusterReplica) {
    return grouping === "Node" ? r.node : grouping === "Application" ? r.application : grouping === "Role" ? r.role : "";
}

/**
 * Flattened out of the app, service, partition hierarchy so "what's primary on node 3" is one filter away. Filters
 * live in the URL, so a slice can be linked to.
 */
export function Replicas() {
    const replicas = useResource("replicas", api.replicas);
    const [view, setView] = useQueryParam<View>("view", "Replicas");
    const [role, setRole] = useQueryParam<Role>("role", "All");
    const [health, setHealth] = useQueryParam<HealthFilter>("health", "All");
    const [grouping, setGrouping] = useQueryParam<Grouping>("group", "None");
    const [search, setSearch] = useQueryParam("q", "");
    const [node, setNode] = useQueryParam("node", "");
    const [app, setApp] = useQueryParam("app", "");
    const [service, setService] = useQueryParam("service", "");
    const [limit, setLimit] = useState(pageSize);
    // A new filter starts from the top again rather than keeping however far the last one was paged.
    const filterKey = [view, role, health, grouping, search, node, app, service].join("\0");
    const [limitFor, setLimitFor] = useState(filterKey);
    if (limitFor !== filterKey) {
        setLimitFor(filterKey);
        setLimit(pageSize);
    }

    return (
        <Load resource={replicas}>
            {(all) => {
                const q = search.toLowerCase();
                const scoped = all.filter((r) => (!node || r.node === node) && (!app || r.application === app) && (!service || r.service === service));
                const shown = scoped.filter(
                    (r) => roleMatches(role, r) && matches(health, view === "Partitions" ? r.partitionHealth : r.health) && (!q || `${r.service} ${r.node} ${r.role} ${r.status} ${r.partitionId}`.toLowerCase().includes(q)),
                );
                const scope = [node && `on ${node}`, app && `in ${app}`, service && `of ${service}`].filter(Boolean).join(" ");
                return (
                    <>
                        <h2 className="sr-only">
                            {plural(shown.length, "replica")} {scope}.
                        </h2>
                        <PageHeader
                            title={scope ? `Replicas ${scope}` : "Replicas"}
                            summary={`${plural(scoped.length, "replica")} · ${plural(new Set(scoped.map((r) => r.partitionId)).size, "partition")}`}
                            onRefresh={replicas.reload}
                            updated={replicas.at}
                            actions={
                                scope && (
                                    <Button
                                        onClick={() => {
                                            setNode("");
                                            setApp("");
                                            setService("");
                                        }}
                                    >
                                        Show the whole cluster
                                    </Button>
                                )
                            }
                        />
                        <Metrics>
                            <MetricCard label="Replicas" value={scoped.length.toLocaleString()} />
                            <MetricCard label="Primaries" value={count(scoped, (r) => r.role === "Primary").toLocaleString()} />
                            <MetricCard label="Partitions" value={new Set(scoped.map((r) => r.partitionId)).size.toLocaleString()} />
                            <MetricCard label="Unhealthy" value={count(scoped, (r) => r.health === "Warning" || r.health === "Error").toLocaleString()} />
                        </Metrics>
                        <Toolbar>
                            <SearchBox value={search} onChange={setSearch} placeholder="Filter by service, node, role, status or partition" />
                            <Chips label="Show" options={views} value={view} onChange={setView} />
                        </Toolbar>
                        <Toolbar>
                            <Chips label="Role" options={roles} value={role} onChange={setRole} />
                            <Chips label="Health" options={healthFilters} value={health} onChange={setHealth} />
                            {view === "Replicas" && <Chips label="Group by" options={groupings} value={grouping} onChange={setGrouping} labels={{ None: "Ungrouped" }} />}
                        </Toolbar>
                        {view === "Replicas" ? (
                            <ReplicaList replicas={shown} total={scoped.length} grouping={grouping} limit={limit} onMore={() => setLimit(limit + pageSize)} />
                        ) : (
                            <PartitionList replicas={shown} />
                        )}
                    </>
                );
            }}
        </Load>
    );
}

function ReplicaRow({ r }: { r: ClusterReplica }) {
    return (
        <LinkRow
            href={href.partition(r.applicationId, r.serviceId, r.partitionId)}
            icon={roleIcon(r.role)}
            name={r.service}
            sub={`${r.role} on ${r.node} · ${r.status} · partition ${r.partitionId.slice(0, 8)}…`}
            status={r.health}
        />
    );
}

function ReplicaList({ replicas, total, grouping, limit, onMore }: { replicas: ClusterReplica[]; total: number; grouping: Grouping; limit: number; onMore: () => void }) {
    const visible = replicas.slice(0, limit);
    const groups = grouping === "None" ? [""] : [...new Set(visible.map((r) => groupKey(grouping, r)))].sort((a, b) => a.localeCompare(b));
    return (
        <Section label="Replicas" aside={replicas.length === total ? plural(total, "replica") : `${replicas.length.toLocaleString()} of ${total.toLocaleString()}`}>
            {replicas.length === 0 && <Empty>No replicas match this filter.</Empty>}
            {groups.map((g) => {
                const members = grouping === "None" ? visible : visible.filter((r) => groupKey(grouping, r) === g);
                return (
                    <div key={g}>
                        {grouping !== "None" && (
                            <GroupHeading
                                label={
                                    <span className="cluster">
                                        {g}
                                        <Pill status={worst(members.map((r) => r.health))} />
                                    </span>
                                }
                                aside={`${plural(members.length, "replica")} · ${count(members, (r) => r.role === "Primary")} primary`}
                            />
                        )}
                        {members.map((r) => (
                            <ReplicaRow key={`${r.partitionId}:${r.replicaId}`} r={r} />
                        ))}
                    </div>
                );
            })}
            {replicas.length > visible.length && (
                <div className="more">
                    <Button onClick={onMore}>Show {Math.min(pageSize, replicas.length - visible.length).toLocaleString()} more</Button>
                    <span className="secondary small">Showing {visible.length.toLocaleString()} of {replicas.length.toLocaleString()}.</span>
                </div>
            )}
        </Section>
    );
}

function PartitionList({ replicas }: { replicas: ClusterReplica[] }) {
    const partitions = new Map<string, ClusterReplica[]>();
    for (const r of replicas) partitions.set(r.partitionId, [...(partitions.get(r.partitionId) ?? []), r]);
    const rows = [...partitions.values()].slice(0, 500);
    return (
        <Section label="Partitions" aside={plural(partitions.size, "partition")}>
            {partitions.size === 0 && <Empty>No partitions match this filter.</Empty>}
            {rows.map((members) => {
                const p = members[0];
                const primary = members.find((m) => m.role === "Primary");
                return (
                    <LinkRow
                        key={p.partitionId}
                        href={href.partition(p.applicationId, p.serviceId, p.partitionId)}
                        icon="layout-grid"
                        name={p.service}
                        sub={`${p.partitionStatus} · ${primary ? `primary on ${primary.node}` : plural(members.length, p.serviceKind === "Stateful" ? "replica" : "instance")} · ${members.map((m) => m.node).join(", ")}`}
                        value={<span className="mono">{p.partitionId.slice(0, 8)}…</span>}
                        status={p.partitionHealth}
                    />
                );
            })}
            {partitions.size > rows.length && <Empty>Showing the first 500. Narrow the filter to see the rest.</Empty>}
        </Section>
    );
}
