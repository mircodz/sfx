import { ActionGroup } from "../actions/ActionGroup";
import { api } from "../lib/api";
import { count, plural, when } from "../lib/format";
import { byHealth, isUnhealthy } from "../lib/health";
import { useResource } from "../lib/resource";
import { useEventsAvailable } from "../lib/session";
import { href } from "../lib/router";
import { isRunning, modeText, upgradeProgress, upgradeWord } from "../lib/upgrades";
import { Link } from "../ui/Button";
import { Document } from "../ui/Document";
import { ErrorsAndWarnings, HealthPanel } from "../ui/Health";
import { Load } from "../ui/Load";
import { ProgressBar } from "../ui/Meter";
import { MetricCard, Metrics } from "../ui/MetricCard";
import { PageHeader } from "../ui/PageHeader";
import { LinkRow } from "../ui/Row";
import { Empty, Section } from "../ui/Section";
import { HealthTab, Tabs, useTab } from "../ui/Tabs";
import { TimelineRow } from "../ui/Timeline";

const tabs = ["overview", "health", "manifest"] as const;

/** The cluster's page: how it is, what's unhealthy, what's running, what just happened. */
export function Overview() {
    const cluster = useResource("cluster", api.cluster);
    const nodes = useResource("nodes", api.nodes);
    const apps = useResource("apps", api.applications);
    const upgrades = useResource("upgrades", api.upgrades);
    const eventsAvailable = useEventsAvailable();
    const events = useResource(`events:1:${eventsAvailable}`, () => (eventsAvailable ? api.events(1) : Promise.resolve({ events: [] })));
    const [tab, setTab] = useTab(tabs);

    return (
        <Load resource={cluster}>
            {(c) => {
                const n = nodes.data ?? [];
                const a = apps.data ?? [];
                const running = (upgrades.data ?? []).filter(isRunning);
                const badNodes = n.filter((x) => isUnhealthy(x.health));
                const badApps = a.filter((x) => isUnhealthy(x.health)).sort((x, y) => byHealth(x.health, y.health));
                return (
                    <>
                        <h2 className="sr-only">
                            Cluster {c.name} is {c.health.health.toLowerCase()}, with {plural(badNodes.length + badApps.length, "unhealthy node or application", "unhealthy nodes and applications")}.
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
                                                note={`${count(n, (x) => x.status === "Down")} down · ${count(n, (x) => x.status.startsWith("Disabl"))} disabled`}
                                            />
                                            <MetricCard label="Applications" value={apps.data ? a.length : "–"} href={href.apps} note={`${badApps.length} unhealthy`} />
                                            <MetricCard label="Upgrades running" value={upgrades.data ? running.length : "–"} href={href.upgrades} />
                                            {eventsAvailable ? (
                                                <MetricCard label="Events" value={events.data?.events.length ?? "–"} href={href.events} note="last hour" />
                                            ) : (
                                                <MetricCard label="Unhealthy nodes" value={nodes.data ? badNodes.length : "–"} href={href.nodes} />
                                            )}
                                        </Metrics>
                                    </div>

                                    <ErrorsAndWarnings evaluations={c.health.evaluations} />

                                    {badNodes.length + badApps.length > 0 && (
                                        <Section label="Unhealthy" aside={plural(badNodes.length + badApps.length, "entity", "entities")}>
                                            {badApps.map((x) => (
                                                <LinkRow key={x.id} href={href.app(x.id)} icon="apps" name={x.name} sub={`Application · ${x.typeName} ${x.typeVersion}`} reason={x.reason} status={x.health} />
                                            ))}
                                            {badNodes.map((x) => (
                                                <LinkRow key={x.name} href={href.node(x.name)} icon="server" name={x.name} sub={`Node · ${x.status} · ${x.nodeType}`} reason={x.reason} status={x.health} />
                                            ))}
                                        </Section>
                                    )}

                                    <div className="two-columns">
                                        <Section label="Upgrades" aside={<Link href={href.upgrades}>All upgrades</Link>}>
                                            {upgrades.data && running.length === 0 && <Empty>No upgrade is running.</Empty>}
                                            {running.map((u) => {
                                                const p = upgradeProgress(u);
                                                return (
                                                    <a key={u.name} className="upgrade-summary" href={href.upgrades}>
                                                        <span className="upgrade-summary-head">
                                                            <span className="strong">{u.name}</span>
                                                            <span className="secondary small">
                                                                {upgradeWord(u)} · {p.done} of {p.total}
                                                            </span>
                                                        </span>
                                                        <span className="secondary small">
                                                            {u.from && <span className="mono">{u.from} to </span>}
                                                            <span className="mono">{u.target}</span> · {modeText(u.mode)}
                                                        </span>
                                                        <ProgressBar pct={p.pct} label={`${u.name} upgrade progress`} />
                                                    </a>
                                                );
                                            })}
                                        </Section>
                                        {eventsAvailable && (
                                            <Section label="Recent events" aside={<Link href={href.events}>All events</Link>}>
                                                {events.data && events.data.events.length === 0 && <Empty>Nothing happened in the last hour.</Empty>}
                                                {events.data?.events.slice(0, 8).map((e, i) => (
                                                    <TimelineRow key={i} time={when(e.time)} title={e.kind} sub={e.entity} />
                                                ))}
                                            </Section>
                                        )}
                                    </div>
                                </>
                            )}
                        </Tabs>
                    </>
                );
            }}
        </Load>
    );
}
