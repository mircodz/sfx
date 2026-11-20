import { useState } from "react";
import { ActionGroup } from "../actions/ActionGroup";
import { api } from "../lib/api";
import { plural, shortName } from "../lib/format";
import { useResource } from "../lib/resource";
import { useEventsAvailable } from "../lib/session";
import { href } from "../lib/router";
import { Document } from "../ui/Document";
import { Fields } from "../ui/Fields";
import { ErrorsAndWarnings, HealthPanel } from "../ui/Health";
import { Load } from "../ui/Load";
import { Meters } from "../ui/Meter";
import { PageHeader } from "../ui/PageHeader";
import { DisclosureRow, RowActions } from "../ui/Row";
import { Section } from "../ui/Section";
import { CountTab, HealthTab, Tabs, useTab } from "../ui/Tabs";
import { EntityEvents } from "./Events";
import { replicaSet } from "./ServiceDetail";

const tabs = ["overview", "health", "events", "details"] as const;

export function roleIcon(role: string) {
    return role === "Primary" ? "crown" : role === "Instance" ? "box" : "copy";
}

export function PartitionDetail({ appId, serviceId, partitionId }: { appId: string; serviceId: string; partitionId: string }) {
    const eventsAvailable = useEventsAvailable();
    const partition = useResource(`partition:${partitionId}`, () => api.partition(appId, serviceId, partitionId));
    const [open, setOpen] = useState<string | null>(null);
    const [tab, setTab] = useTab(tabs);

    return (
        <Load resource={partition}>
            {(d) => {
                const p = d.partition;
                const s = d.service;
                const appName = s.name.split("/").slice(0, -1).join("/");
                const noun = s.kind === "Stateful" ? "replica" : "instance";
                return (
                    <>
                        <h2 className="sr-only">
                            Partition {p.id} of {s.name} is {p.status.toLowerCase()} and {p.health.toLowerCase()}, with {plural(d.replicas.length, noun)}.
                        </h2>
                        <PageHeader
                            title={<span className="mono">{p.id}</span>}
                            summary={`${p.scheme} · ${p.status} · ${replicaSet(s.kind, p.replicaSet)}`}
                            crumbs={[
                                { label: "Applications", href: href.apps },
                                { label: appName, href: href.app(appId) },
                                { label: shortName(s.name, appName), href: href.service(appId, serviceId) },
                            ]}
                            status={p.health}
                            onRefresh={partition.reload}
                            updated={partition.at}
                            actions={<ActionGroup actions={d.actions} label="Partition actions" />}
                        />
                        <Tabs
                            value={tab}
                            onChange={setTab}
                            tabs={[
                                { id: "overview", label: <CountTab label={s.kind === "Stateful" ? "Replicas" : "Instances"} count={d.replicas.length} /> },
                                { id: "health", label: <HealthTab health={d.health.health} /> },
                                { id: "events", label: "Events", hidden: !eventsAvailable },
                                { id: "details", label: "Details" },
                            ]}
                        >
                            {tab === "overview" && (
                                <>
                                    <ErrorsAndWarnings evaluations={d.health.evaluations} />
                                    <Section label={s.kind === "Stateful" ? "Replicas" : "Instances"} aside={plural(d.replicas.length, noun)}>
                                        {d.replicas.map((r) => (
                                            <DisclosureRow
                                                key={r.id}
                                                id={r.id}
                                                icon={roleIcon(r.role)}
                                                name={
                                                    <>
                                                        {r.role} <span className="secondary">on</span> {r.nodeName}
                                                    </>
                                                }
                                                sub={`${r.status} · id ${r.id}`}
                                                status={r.health}
                                                open={open === r.id}
                                                onToggle={() => setOpen((o) => (o === r.id ? null : r.id))}
                                            >
                                                <Fields
                                                    fields={[
                                                        { name: "Id", value: r.id },
                                                        { name: "Role", value: r.role },
                                                        { name: "Status", value: r.status },
                                                        { name: "Node", value: r.nodeName, href: href.node(r.nodeName) },
                                                        { name: "Address", value: r.address },
                                                    ]}
                                                />
                                                {r.actions.length > 0 && (
                                                    <RowActions>
                                                        <ActionGroup actions={r.actions} label={`Actions for ${noun} ${r.id}`} />
                                                    </RowActions>
                                                )}
                                            </DisclosureRow>
                                        ))}
                                    </Section>
                                    <Section label="Load">
                                        <Meters metrics={d.load} />
                                    </Section>
                                </>
                            )}
                            {tab === "health" && <HealthPanel health={d.health} />}
                            {tab === "events" && <EntityEvents entity={p.id} />}
                            {tab === "details" && <Document cacheKey={`partition-raw:${partitionId}`} load={() => api.partitionRaw(appId, serviceId, partitionId)} name={partitionId} />}
                        </Tabs>
                    </>
                );
            }}
        </Load>
    );
}
