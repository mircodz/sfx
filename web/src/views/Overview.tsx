import { ActionGroup } from "../actions/ActionGroup";
import { api } from "../lib/api";
import { count, metricName, number, plural, when } from "../lib/format";
import { byHealth, isUnhealthy } from "../lib/health";
import { useOperations } from "../lib/operations";
import { useResource } from "../lib/resource";
import { href } from "../lib/router";
import { useEventsAvailable } from "../lib/session";
import { direction, upgradeProgress, upgradeWord } from "../lib/upgrades";
import { Link } from "../ui/Button";
import { Card, Dashboard } from "../ui/Card";
import { Document } from "../ui/Document";
import { ErrorsAndWarnings, HealthPanel } from "../ui/Health";
import { HealthBar } from "../ui/HealthBar";
import { Load } from "../ui/Load";
import { ProgressBar } from "../ui/Meter";
import { MetricCard, Metrics } from "../ui/MetricCard";
import { NodeMap } from "../ui/NodeMap";
import { PageHeader } from "../ui/PageHeader";
import { LinkRow } from "../ui/Row";
import { Empty } from "../ui/Section";
import { HealthTab, Tabs, useTab } from "../ui/Tabs";
import { TimelineRow } from "../ui/Timeline";
import { DomainStrip } from "../ui/DomainStrip";

const tabs = ["overview", "health", "manifest"] as const;

/** "395 up · 3 disabled · 2 down": every status that occurs, most common first. */
function nodeStatusNote(nodes: { status: string }[]) {
    const by = new Map<string, number>();
    for (const n of nodes) by.set(n.status, (by.get(n.status) ?? 0) + 1);
    return [...by.entries()]
        .sort((a, b) => b[1] - a[1])
        .map(([status, n]) => `${n.toLocaleString()} ${status.toLowerCase()}`)
        .join(" · ");
}

/**
 * The cluster at a glance, for a wide screen: counts, health by kind, what's wrong and why, the nodes by upgrade
 * domain, upgrades, load, and what just happened. Every tile links to the page with the rest.
 */
export function Overview() {
    const cluster = useResource("cluster", api.cluster);
    const nodes = useResource("nodes", api.nodes);
    const apps = useResource("apps", api.applications);
    // Counts, load and running upgrades in a handful of calls however big the cluster is.
    const overview = useResource("overview", api.overview);
    const activity = useResource("activity", api.activity);
    const eventsAvailable = useEventsAvailable();
    const events = useResource(`events:1:${eventsAvailable}`, () => (eventsAvailable ? api.events(1) : Promise.resolve({ events: [] })));
    const operations = useOperations();
    const [tab, setTab] = useTab(tabs);

    return (
        <Load resource={cluster} shape="dashboard">
            {(c) => {
                const n = nodes.data ?? [];
                const a = apps.data ?? [];
                const o = overview.data;
                const counts = Object.fromEntries((o?.counts ?? []).map((c) => [c.kind, c]));
                const total = (kind: string) => (counts[kind] ? counts[kind].healthy + counts[kind].warning + counts[kind].error : undefined);
                const tally = (kind: string) => (!o ? undefined : counts[kind] ? { Healthy: counts[kind].healthy, Warning: counts[kind].warning, Error: counts[kind].error } : {});
                const running = o?.runningUpgrades ?? [];
                const badNodes = n.filter((x) => isUnhealthy(x.health));
                const badApps = a.filter((x) => isUnhealthy(x.health)).sort((x, y) => byHealth(x.health, y.health));
                const capped = (o?.load ?? []).filter((m) => m.clusterCapacity);
                // One line per change: running ones from this connection, then the log, where a change can have a
                // "requested" and a "done" line; keep the newest line per change.
                const seen = new Set(operations.map((o) => o.title));
                const logged: { time: string; title: string; status: "Done" | "Failed" | "In progress" }[] = [];
                for (const e of activity.data ?? []) {
                    if (seen.has(e.action)) continue;
                    seen.add(e.action);
                    logged.push({ time: e.time, title: e.action, status: e.succeeded ? "Done" : "Failed" });
                }
                const recent = [
                    ...operations.map((o) => ({ time: o.started, title: o.title, status: o.state === "running" ? ("In progress" as const) : o.state === "done" ? ("Done" as const) : ("Failed" as const) })),
                    ...logged,
                ]
                    .sort((x, y) => new Date(y.time).getTime() - new Date(x.time).getTime())
                    .slice(0, 6);

                return (
                    <>
                        <h2 className="sr-only">
                            Cluster {c.name} is {c.health.health.toLowerCase()}: {badApps.length} unhealthy applications, {badNodes.length} unhealthy nodes.
                        </h2>
                        <PageHeader
                            title={c.name}
                            summary={`${c.endpoint} · version ${c.codeVersion}`}
                            status={c.health.health}
                            onRefresh={cluster.reload}
                            updated={cluster.at}
                            actions={<ActionGroup actions={c.actions} label="Cluster actions" />}
                        />
                        <Tabs
                            value={tab}
                            onChange={setTab}
                            tabs={[
                                { id: "overview", label: "Overview" },
                                { id: "health", label: <HealthTab health={c.health.health} /> },
                                { id: "manifest", label: "Manifest" },
                            ]}
                        >
                            {tab === "health" && <HealthPanel health={c.health} />}
                            {tab === "manifest" && <Document cacheKey="cluster-manifest" load={api.clusterManifest} name="ClusterManifest" />}
                            {tab === "overview" && (
                                <>
                                    <div className="section">
                                        <Metrics>
                                            <MetricCard
                                                label="Nodes"
                                                value={nodes.data ? n.length : "–"}
                                                href={href.nodes}
                                                note={nodes.data ? nodeStatusNote(n) : undefined}
                                            />
                                            <MetricCard label="Applications" value={apps.data ? a.length : "–"} href={href.apps} note={apps.data ? `${badApps.length} unhealthy` : undefined} />
                                            <MetricCard label="Services" value={total("Service")?.toLocaleString() ?? "–"} href={href.apps} />
                                            <MetricCard label="Partitions" value={total("Partition")?.toLocaleString() ?? "–"} href={href.replicas({ view: "Partitions" })} />
                                            <MetricCard label="Replicas" value={total("Replica")?.toLocaleString() ?? "–"} href={href.replicas()} />
                                            <MetricCard label="Upgrades running" value={o ? running.length : "–"} href={href.upgrades} />
                                        </Metrics>
                                    </div>

                                    <Dashboard>
                                        <Card title="Health" href={`${href.overview}?tab=health`} span={4}>
                                            <HealthBar label="Nodes" counts={tally("Node")} href={href.nodes} />
                                            <HealthBar label="Applications" counts={tally("Application")} href={href.apps} />
                                            <HealthBar label="Services" counts={tally("Service")} />
                                            <HealthBar label="Partitions" counts={tally("Partition")} href={href.replicas({ view: "Partitions" })} />
                                            <HealthBar label="Replicas" counts={tally("Replica")} href={href.replicas()} />
                                            <ErrorsAndWarnings evaluations={c.health.evaluations} href={`${href.overview}?tab=health`} />
                                        </Card>

                                        <Card title="Unhealthy" href={`${href.apps}?health=Unhealthy`} aside={plural(badApps.length + badNodes.length, "entity", "entities")} span={8}>
                                            {badApps.length + badNodes.length === 0 && <Empty>Everything is healthy.</Empty>}
                                            {badApps.slice(0, 8).map((x) => (
                                                <LinkRow key={x.id} href={href.app(x.id)} icon="apps" name={x.name} sub={`${x.typeName} ${x.typeVersion}`} reason={x.reason} status={x.health} />
                                            ))}
                                            {badNodes.slice(0, 4).map((x) => (
                                                <LinkRow key={x.name} href={href.node(x.name)} icon="server" name={x.name} sub={`${x.status} · ${x.nodeType}`} reason={x.reason} status={x.health} />
                                            ))}
                                            {badApps.length > 8 && (
                                                <p className="card-more">
                                                    <Link href={`${href.apps}?health=Unhealthy`}>{plural(badApps.length - 8, "more application")}</Link>
                                                </p>
                                            )}
                                        </Card>

                                        <Card title="Nodes" href={`${href.nodes}?layout=Map`} aside={nodes.data ? plural(n.length, "node") : undefined} span={5}>
                                            {nodes.data ? <NodeMap nodes={n} grouped={false} selected={null} onSelect={(name) => name && (location.hash = href.node(name))} /> : <Empty>Loading…</Empty>}
                                            {nodes.data && (
                                                <p className="card-facts">
                                                    {plural(new Set(n.map((x) => x.nodeType)).size, "node type")} · {plural(new Set(n.map((x) => x.upgradeDomain)).size, "upgrade domain")} ·{" "}
                                                    {plural(new Set(n.map((x) => x.faultDomain)).size, "fault domain")} · {plural(count(n, (x) => x.isSeed), "seed node")}
                                                </p>
                                            )}
                                        </Card>

                                        <Card title="Upgrades" href={href.upgrades} aside={o ? `${running.length} running` : undefined} span={7}>
                                            {o && running.length === 0 && <Empty>No upgrade is running.</Empty>}
                                            {running.map((u) => {
                                                const p = upgradeProgress(u);
                                                return (
                                                    <a key={u.name} className="upgrade-summary" href={href.upgrades}>
                                                        <span className="upgrade-summary-head">
                                                            <span className="strong">
                                                                {u.name} <span className="mono secondary">{direction(u).from ? `${direction(u).from} to ${direction(u).to}` : direction(u).to}</span>
                                                            </span>
                                                            <span className="secondary small">
                                                                {upgradeWord(u)} · {p.done} of {p.total}
                                                            </span>
                                                        </span>
                                                        <DomainStrip upgrade={u} />
                                                    </a>
                                                );
                                            })}
                                        </Card>

                                        <Card title="Load" href={`${href.nodes}?tab=load`} span={4}>
                                            {o && capped.length === 0 && <Empty>No metric has a capacity to compare with.</Empty>}
                                            <div className="meters">
                                                {capped.map((m) => {
                                                    const pct = Math.round((m.clusterLoad / m.clusterCapacity!) * 100);
                                                    return (
                                                        <div key={m.name}>
                                                            <div className="meter-head">
                                                                <span>{metricName(m.name)}</span>
                                                                <span>
                                                                    {number(m.clusterLoad)} of {number(m.clusterCapacity!)} · {pct}%
                                                                </span>
                                                            </div>
                                                            <ProgressBar pct={pct} label={`${m.name} ${pct}%`} warnAbove={85} />
                                                            {m.maxNode && m.maxLoad !== m.minLoad && (
                                                                <div className="meter-foot">
                                                                    Busiest {m.maxNode} at {number(m.maxLoad ?? 0)} · quietest {m.minNode} at {number(m.minLoad ?? 0)}
                                                                </div>
                                                            )}
                                                        </div>
                                                    );
                                                })}
                                            </div>
                                        </Card>

                                        <Card title="Recent changes" href={href.activity} span={eventsAvailable ? 4 : 8}>
                                            {recent.length === 0 && <Empty>Nothing changed through sfx yet.</Empty>}
                                            {recent.map((e, i) => (
                                                <TimelineRow key={i} time={when(e.time)} title={e.title} status={e.status} />
                                            ))}
                                        </Card>

                                        {eventsAvailable && (
                                            <Card title="Events" href={href.events} aside="Last hour" span={4}>
                                                {events.data && events.data.events.length === 0 && <Empty>Nothing happened in the last hour.</Empty>}
                                                {events.data?.events.slice(0, 6).map((e, i) => (
                                                    <TimelineRow key={i} time={when(e.time)} title={e.kind} sub={e.entity} />
                                                ))}
                                            </Card>
                                        )}

                                    </Dashboard>
                                </>
                            )}
                        </Tabs>
                    </>
                );
            }}
        </Load>
    );
}
