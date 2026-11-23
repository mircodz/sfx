import { ActionGroup } from "../actions/ActionGroup";
import { api } from "../lib/api";
import { duration, partitionKey, plural, shortName } from "../lib/format";
import { useResource } from "../lib/resource";
import { useEventsAvailable } from "../lib/session";
import { href } from "../lib/router";
import { Link } from "../ui/Button";
import { Document } from "../ui/Document";
import { Fields } from "../ui/Fields";
import { ErrorsAndWarnings, HealthPanel } from "../ui/Health";
import { Load } from "../ui/Load";
import { PageHeader } from "../ui/PageHeader";
import { LinkRow } from "../ui/Row";
import { Section } from "../ui/Section";
import { CountTab, HealthTab, Tabs, useTab } from "../ui/Tabs";
import { EntityEvents } from "./Events";
import { Limited } from "../ui/ShowMore";
import { Placement } from "./Placement";
import { Split } from "../ui/Split";

const tabs = ["overview", "placement", "health", "events", "manifest", "details"] as const;

export function replicaSet(kind: string, set: string) {
    return kind === "Stateful" ? `replicas ${set} (target/min)` : `instances ${set}`;
}

export function ServiceDetail({ appId, serviceId }: { appId: string; serviceId: string }) {
    const eventsAvailable = useEventsAvailable();
    const service = useResource(`service:${serviceId}`, () => api.service(appId, serviceId));
    const [tab, setTab] = useTab(tabs);
    const appName = `fabric:/${appId.replaceAll("~", "/")}`;

    return (
        <Load resource={service}>
            {(d) => {
                const s = d.service;
                return (
                    <>
                        <h2 className="sr-only">
                            Service {s.name} is {s.health.toLowerCase()}, with {plural(d.partitions.length, "partition")}.
                        </h2>
                        <PageHeader
                            title={shortName(s.name, appName)}
                            summary={`${s.kind} · ${s.typeName} · ${s.status}`}
                            crumbs={[
                                { label: "Applications", href: href.apps },
                                { label: appName, href: href.app(appId) },
                            ]}
                            status={s.health}
                            onRefresh={service.reload}
                            updated={service.at}
                            actions={<ActionGroup actions={d.actions} context={{ service: { appId, serviceId } }} label="Service actions" />}
                        />
                        <Tabs
                            value={tab}
                            onChange={setTab}
                            tabs={[
                                { id: "overview", label: <CountTab label="Partitions" count={d.partitions.length} /> },
                                { id: "placement", label: "Placement" },
                                { id: "health", label: <HealthTab health={d.health.health} /> },
                                { id: "events", label: "Events", hidden: !eventsAvailable },
                                { id: "manifest", label: "Manifest", hidden: appId === "System" },
                                { id: "details", label: "Details" },
                            ]}
                        >
                            {tab === "overview" && (
                                <Split
                                    aside={
                                        <Section label="Description">
                                            <Fields fields={d.description.map((f) => ({ ...f, value: seconds(f.value) }))} />
                                        </Section>
                                    }
                                >
                                    <ErrorsAndWarnings evaluations={d.health.evaluations} />
                                    <Section label="Partitions" aside={<Link href={href.replicas({ service: s.name })}>Replicas</Link>}>
                                        <Limited items={byKey(d.partitions)}>
                                            {(p) => (
                                            <LinkRow
                                                key={p.id}
                                                href={href.partition(appId, serviceId, p.id)}
                                                icon="layout-grid"
                                                name={partitionKey(p) === p.scheme ? <span className="mono">{p.id}</span> : partitionKey(p)}
                                                sub={`${partitionKey(p) === p.scheme ? "" : `${p.id.slice(0, 8)}… · `}${p.status} · ${replicaSet(s.kind, p.replicaSet)}`}
                                                reason={p.reason}
                                                status={p.health}
                                            />
                                            )}
                                        </Limited>
                                    </Section>
                                </Split>
                            )}
                            {tab === "placement" && <Placement appId={appId} serviceId={serviceId} />}
                            {tab === "health" && <HealthPanel health={d.health} />}
                            {tab === "events" && <EntityEvents entity={s.name} />}
                            {tab === "manifest" && <Document cacheKey={`service-manifest:${serviceId}`} load={() => api.serviceManifest(appId, serviceId)} name={s.typeName} />}
                            {tab === "details" && <Document cacheKey={`service-raw:${serviceId}`} load={() => api.serviceRaw(appId, serviceId)} name={serviceId} />}
                        </Tabs>
                    </>
                );
            }}
        </Load>
    );
}

/** Ranged partitions in key order rather than by id. Keys are 64-bit, so compared as BigInt. */
function byKey<T extends { lowKey?: string | null }>(partitions: T[]) {
    if (!partitions.every((p) => p.lowKey != null && /^-?\d+$/.test(p.lowKey))) return partitions;
    return [...partitions].sort((a, b) => (BigInt(a.lowKey!) < BigInt(b.lowKey!) ? -1 : BigInt(a.lowKey!) > BigInt(b.lowKey!) ? 1 : 0));
}

/** "1800s" as "30m"; a wait of decades is SF's way of saying never. */
function seconds(value: string) {
    const m = /^(\d+)s$/.exec(value);
    if (!m) return value;
    const n = Number(m[1]);
    return n >= 10 * 365 * 86400 ? "No limit" : duration(n);
}
