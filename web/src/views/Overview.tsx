import { ActionGroup } from "../actions/ActionGroup";
import { api } from "../lib/api";
import { count, number, plural, when } from "../lib/format";
import { byHealth, isUnhealthy } from "../lib/health";
import { useOperations } from "../lib/operations";
import { useResource } from "../lib/resource";
import { href } from "../lib/router";
import { useEventsAvailable } from "../lib/session";
import { isRunning, upgradeProgress, upgradeWord } from "../lib/upgrades";
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

/**
 * The cluster at a glance, for a wide screen: counts, health by kind, what's wrong and why, the nodes by upgrade
 * domain, upgrades, load, and what just happened. Every tile links to the page with the rest.
 */
export function Overview() {
    const cluster = useResource("cluster", api.cluster);
    const nodes = useResource("nodes", api.nodes);
    const apps = useResource("apps", api.applications);
    const upgrades = useResource("upgrades", api.upgrades);
    const replicas = useResource("replicas", api.replicas);
    const load = useResource("load", api.load);
    const activity = useResource("activity", api.activity);
    const eventsAvailable = useEventsAvailable();
    const events = useResource(`events:1:${eventsAvailable}`, () => (eventsAvailable ? api.events(1) : Promise.resolve({ events: [] })));
    const operations = useOperations();
    const [tab, setTab] = useTab(tabs);

    return (
        <Load resource={cluster}>
            {(c) => {
                const n = nodes.data ?? [];
                const a = apps.data ?? [];
                const r = replicas.data ?? [];
                const partitions = new Map(r.map((x) => [x.partitionId, x.partitionHealth]));
                const services = new Set(r.map((x) => x.serviceId));
                const running = (upgrades.data ?? []).filter(isRunning);
                const badNodes = n.filter((x) => isUnhealthy(x.health));
                const badApps = a.filter((x) => isUnhealthy(x.health)).sort((x, y) => byHealth(x.health, y.health));
                const capped = (load.data?.metrics ?? []).filter((m) => m.clusterCapacity);
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
                                                note={`${count(n, (x) => x.status === "Up")} up · ${count(n, (x) => x.status === "Down")} down`}
                                            />
                                            <MetricCard label="Applications" value={apps.data ? a.length : "–"} href={href.apps} note={`${badApps.length} unhealthy`} />
                                            <MetricCard label="Services" value={replicas.data ? services.size : "–"} href={href.apps} />
                                            <MetricCard label="Partitions" value={replicas.data ? partitions.size.toLocaleString() : "–"} href={href.replicas({ view: "Partitions" })} />
                                            <MetricCard label="Replicas" value={replicas.data ? r.length.toLocaleString() : "–"} href={href.replicas()} note={`${count(r, (x) => x.role === "Primary")} primary`} />
                                            <MetricCard label="Upgrades running" value={upgrades.data ? running.length : "–"} href={href.upgrades} />
                                        </Metrics>
                                    </div>

                                    <Dashboard>
                                        <Card title="Health" aside={<Link href={`${href.overview}?tab=health`}>Details</Link>} span={4}>
                                            <HealthBar label="Nodes" healths={n.map((x) => x.health)} href={href.nodes} />
                                            <HealthBar label="Applications" healths={a.map((x) => x.health)} href={href.apps} />
                                            <HealthBar label="Partitions" healths={[...partitions.values()]} href={href.replicas({ view: "Partitions" })} />
                                            <HealthBar label="Replicas" healths={r.map((x) => x.health)} href={href.replicas()} />
                                            <ErrorsAndWarnings evaluations={c.health.evaluations} />
                                        </Card>

                                        <Card title="Unhealthy" aside={plural(badApps.length + badNodes.length, "entity", "entities")} span={8}>
                                            {badApps.length + badNodes.length === 0 && <Empty>Everything is healthy.</Empty>}
                                            {badApps.slice(0, 8).map((x) => (
                                                <LinkRow key={x.id} href={href.app(x.id)} icon="apps" name={x.name} sub={`${x.typeName} ${x.typeVersion}`} reason={x.reason} status={x.health} />
                                            ))}
                                            {badNodes.slice(0, 4).map((x) => (
                                                <LinkRow key={x.name} href={href.node(x.name)} icon="server" name={x.name} sub={`${x.status} · ${x.nodeType}`} reason={x.reason} status={x.health} />
                                            ))}
                                            {badApps.length > 8 && (
                                                <p className="card-more">
                                                    <Link href={`${href.apps}?health=Error`}>{plural(badApps.length - 8, "more application")}</Link>
                                                </p>
                                            )}
                                        </Card>

                                        <Card title="Nodes" aside={<Link href={`${href.nodes}?layout=Map`}>Map</Link>} span={5}>
                                            {nodes.data ? <NodeMap nodes={n} grouped={false} selected={null} onSelect={(name) => name && (location.hash = href.node(name))} /> : <Empty>Loading…</Empty>}
                                            {nodes.data && (
                                                <p className="card-facts">
                                                    {plural(new Set(n.map((x) => x.nodeType)).size, "node type")} · {plural(new Set(n.map((x) => x.upgradeDomain)).size, "upgrade domain")} ·{" "}
                                                    {plural(new Set(n.map((x) => x.faultDomain)).size, "fault domain")} · {plural(count(n, (x) => x.isSeed), "seed node")}
                                                </p>
                                            )}
                                        </Card>

                                        <Card title="Upgrades" aside={<Link href={href.upgrades}>All upgrades</Link>} span={7}>
                                            {upgrades.data && running.length === 0 && (
                                                <Empty>
                                                    No upgrade is running.
                                                    {upgrades.data[0] && ` Last: ${upgrades.data.find((u) => !isRunning(u))?.name ?? ""}, ${upgradeWord(upgrades.data.find((u) => !isRunning(u)) ?? upgrades.data[0]).toLowerCase()}.`}
                                                </Empty>
                                            )}
                                            {running.map((u) => {
                                                const p = upgradeProgress(u);
                                                return (
                                                    <a key={u.name} className="upgrade-summary" href={href.upgrades}>
                                                        <span className="upgrade-summary-head">
                                                            <span className="strong">
                                                                {u.name} <span className="mono secondary">{u.from ? `${u.from} to ${u.target}` : u.target}</span>
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

                                        <Card title="Load" aside={<Link href={`${href.nodes}?tab=load`}>By node</Link>} span={4}>
                                            {load.data && capped.length === 0 && <Empty>No metric has a capacity to compare with.</Empty>}
                                            <div className="meters">
                                                {capped.map((m) => {
                                                    const pct = Math.round((m.clusterLoad / m.clusterCapacity!) * 100);
                                                    return (
                                                        <div key={m.name}>
                                                            <div className="meter-head">
                                                                <span>{m.name.replace(/^servicefabric:\/_/, "")}</span>
                                                                <span>
                                                                    {number(m.clusterLoad)} of {number(m.clusterCapacity!)} · {pct}%
                                                                </span>
                                                            </div>
                                                            <ProgressBar pct={pct} label={`${m.name} ${pct}%`} warnAbove={85} />
                                                        </div>
                                                    );
                                                })}
                                            </div>
                                        </Card>

                                        <Card title="Recent changes" aside={<Link href={href.activity}>Activity</Link>} span={eventsAvailable ? 4 : 8}>
                                            {recent.length === 0 && <Empty>Nothing changed through sfx yet.</Empty>}
                                            {recent.map((e, i) => (
                                                <TimelineRow key={i} time={when(e.time)} title={e.title} status={e.status} />
                                            ))}
                                        </Card>

                                        {eventsAvailable && (
                                            <Card title="Events" aside={<Link href={href.events}>All events</Link>} span={4}>
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
