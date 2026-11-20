import { ActionGroup } from "../actions/ActionGroup";
import { api } from "../lib/api";
import { duration, plural } from "../lib/format";
import { useResource } from "../lib/resource";
import { useEventsAvailable } from "../lib/session";
import { href, idOf, useQueryParam } from "../lib/router";
import { Link } from "../ui/Button";
import { Document } from "../ui/Document";
import { Fields } from "../ui/Fields";
import { ErrorsAndWarnings, HealthPanel } from "../ui/Health";
import { Load } from "../ui/Load";
import { Meters } from "../ui/Meter";
import { PageHeader } from "../ui/PageHeader";
import { LinkRow } from "../ui/Row";
import { Empty, GroupHeading, Section } from "../ui/Section";
import { HealthTab, Tabs, useTab } from "../ui/Tabs";
import { SearchBox } from "../ui/Toolbar";
import { EntityEvents } from "./Events";
import { CodePackages } from "./CodePackages";
import { nodeFacts } from "./Nodes";

/** Up, but also disabling or disabled nodes, still run what's on them; only down ones (or unknown) don't. */
export const hosting = (status: string) => !["Down", "Invalid", "Unknown"].includes(status);

const tabs = ["overview", "packages", "health", "events", "details"] as const;

export function NodeDetail({ name }: { name: string }) {
    const eventsAvailable = useEventsAvailable();
    const node = useResource(`node:${name}`, () => api.node(name));
    const [tab, setTab] = useTab(tabs);
    const [search, setSearch] = useQueryParam("q", "");

    return (
        <Load resource={node}>
            {(d) => {
                const n = d.node;
                const q = search.toLowerCase();
                const replicas = d.replicas.filter((r) => !q || `${r.service} ${r.role} ${r.application}`.toLowerCase().includes(q));
                const apps = [...new Set(replicas.map((r) => r.applicationId))];
                return (
                    <>
                        <h2 className="sr-only">
                            Node {n.name} is {n.status.toLowerCase()} and {n.health.toLowerCase()}, hosting {plural(d.replicas.length, "replica")}.
                        </h2>
                        <PageHeader
                            title={n.name}
                            summary={`${nodeFacts(n)}`}
                            crumbs={[{ label: "Nodes", href: href.nodes }]}
                            status={n.health}
                            onRefresh={node.reload}
                            updated={node.at}
                            actions={<ActionGroup actions={n.actions} label="Node actions" />}
                        />
                        <Tabs
                            value={tab}
                            onChange={setTab}
                            tabs={[
                                { id: "overview", label: "Overview" },
                                { id: "packages", label: "Code packages", hidden: !hosting(n.status) },
                                { id: "health", label: <HealthTab health={d.health.health} /> },
                                { id: "events", label: "Events", hidden: !eventsAvailable },
                                { id: "details", label: "Details" },
                            ]}
                        >
                            {tab === "overview" && (
                                <>
                                    <ErrorsAndWarnings evaluations={d.health.evaluations} />
                                    <Section label="Facts">
                                        <Fields
                                            fields={[
                                                { name: "Address", value: n.address },
                                                { name: "Status", value: n.status },
                                                { name: "Node type", value: n.nodeType },
                                                { name: "Upgrade domain", value: n.upgradeDomain },
                                                { name: "Fault domain", value: n.faultDomain },
                                                { name: "Seed node", value: n.isSeed ? "Yes" : "No" },
                                                { name: "Version", value: n.codeVersion },
                                                { name: "Up for", value: n.uptimeSeconds ? duration(n.uptimeSeconds) : null },
                                            ]}
                                        />
                                    </Section>
                                    <Section label="Load">
                                        <Meters metrics={d.load} />
                                    </Section>
                                    <Section label="Replicas on this node" aside={<Link href={href.replicas({ node: n.name })}>{plural(d.replicas.length, "replica")}</Link>}>
                                        {!hosting(n.status) && <Empty>The node is {n.status.toLowerCase()}, so nothing runs on it.</Empty>}
                                        {d.replicas.length > 0 && <SearchBox value={search} onChange={setSearch} placeholder="Filter by service, role or application" />}
                                        {hosting(n.status) && replicas.length === 0 && <Empty>No replicas match.</Empty>}
                                        {apps.map((appId) => {
                                            const group = replicas.filter((r) => r.applicationId === appId);
                                            return (
                                                <div key={appId}>
                                                    <GroupHeading label={group[0].application} aside={plural(group.length, "replica")} />
                                                    {group.map((r) => (
                                                        <LinkRow
                                                            key={`${r.partitionId}:${r.id}`}
                                                            href={href.partition(appId, idOf(r.service), r.partitionId)}
                                                            icon={r.role === "Primary" ? "crown" : r.role === "Instance" ? "box" : "copy"}
                                                            name={r.service}
                                                            sub={`${r.role} · ${r.status} · partition ${r.partitionId.slice(0, 8)}…`}
                                                        />
                                                    ))}
                                                </div>
                                            );
                                        })}
                                    </Section>
                                </>
                            )}
                            {tab === "packages" && <CodePackages node={n.name} />}
                            {tab === "health" && <HealthPanel health={d.health} />}
                            {tab === "events" && <EntityEvents entity={n.name} />}
                            {tab === "details" && <Document cacheKey={`node-raw:${name}`} load={() => api.nodeRaw(name)} name={name} />}
                        </Tabs>
                    </>
                );
            }}
        </Load>
    );
}
