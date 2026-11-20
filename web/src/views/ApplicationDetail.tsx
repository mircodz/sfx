import { ActionGroup } from "../actions/ActionGroup";
import { api } from "../lib/api";
import { plural, shortName } from "../lib/format";
import { useResource } from "../lib/resource";
import { useEventsAvailable } from "../lib/session";
import { href } from "../lib/router";
import { Link } from "../ui/Button";
import { Document } from "../ui/Document";
import { ErrorsAndWarnings, HealthPanel } from "../ui/Health";
import { Load } from "../ui/Load";
import { PageHeader } from "../ui/PageHeader";
import { LinkRow } from "../ui/Row";
import { Empty, Section } from "../ui/Section";
import { CountTab, HealthTab, Tabs, useTab } from "../ui/Tabs";
import { EntityEvents } from "./Events";
import { isRunning } from "../lib/upgrades";
import { Parameters } from "./Parameters";
import { UpgradeCard } from "./UpgradeCard";

const tabs = ["overview", "health", "events", "manifest", "details"] as const;

export function ApplicationDetail({ appId }: { appId: string }) {
    const eventsAvailable = useEventsAvailable();
    const app = useResource(`app:${appId}`, () => api.application(appId));
    const [tab, setTab] = useTab(tabs);
    const system = appId === "System";

    return (
        <Load resource={app}>
            {(d) => {
                const a = d.application;
                return (
                    <>
                        <h2 className="sr-only">
                            Application {a.name} is {a.health.toLowerCase()}, with {plural(d.services.length, "service")}.
                        </h2>
                        <PageHeader
                            title={a.name}
                            summary={[a.typeName, a.typeVersion, a.status].filter(Boolean).join(" · ")}
                            crumbs={[{ label: "Applications", href: href.apps }]}
                            status={a.health}
                            onRefresh={app.reload}
                            updated={app.at}
                            actions={<ActionGroup actions={d.actions} context={{ application: d }} label="Application actions" />}
                        />
                        <Tabs
                            value={tab}
                            onChange={setTab}
                            tabs={[
                                { id: "overview", label: <CountTab label="Services" count={d.services.length} /> },
                                { id: "health", label: <HealthTab health={d.health.health} /> },
                                { id: "events", label: "Events", hidden: !eventsAvailable },
                                { id: "manifest", label: "Manifest", hidden: system },
                                { id: "details", label: "Details" },
                            ]}
                        >
                            {tab === "overview" && (
                                <>
                                    <ErrorsAndWarnings evaluations={d.health.evaluations} />
                                    {d.upgrade && (isRunning(d.upgrade) || d.upgrade.state === "Failed") && (
                                        <Section label="Upgrade">
                                            <UpgradeCard upgrade={d.upgrade} />
                                        </Section>
                                    )}
                                    <Section label="Services" aside={<Link href={href.replicas({ app: a.name })}>Replicas</Link>}>
                                        {d.services.length === 0 && <Empty>No services.</Empty>}
                                        {d.services.map((s) => (
                                            <LinkRow
                                                key={s.id}
                                                href={href.service(appId, s.id)}
                                                icon={s.kind === "Stateful" ? "database" : "box"}
                                                name={shortName(s.name, a.name)}
                                                sub={`${s.kind} · ${s.typeName} · ${s.status}`}
                                                reason={s.reason}
                                                status={s.health}
                                            />
                                        ))}
                                    </Section>
                                    {!system && <Parameters parameters={d.parameters} />}
                                </>
                            )}
                            {tab === "health" && <HealthPanel health={d.health} />}
                            {tab === "events" && <EntityEvents entity={a.name} />}
                            {tab === "manifest" && <Document cacheKey={`app-manifest:${appId}:${a.typeVersion}`} load={() => api.applicationManifest(appId)} name={`${a.typeName}-${a.typeVersion}`} />}
                            {tab === "details" && <Document cacheKey={`app-raw:${appId}`} load={() => api.applicationRaw(appId)} name={appId} />}
                        </Tabs>
                    </>
                );
            }}
        </Load>
    );
}
