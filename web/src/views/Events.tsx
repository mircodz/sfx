import { Fragment } from "react";
import { api } from "../lib/api";
import { day, plural, when } from "../lib/format";
import { useResource } from "../lib/resource";
import { appIdOfService, href, idOf, useQueryParam } from "../lib/router";
import type { ClusterEvent, EntityKind } from "../lib/types";
import { Chips } from "../ui/Chips";
import { Load } from "../ui/Load";
import { PageHeader } from "../ui/PageHeader";
import { Empty, Section } from "../ui/Section";
import { DayHeading, TimelineRow } from "../ui/Timeline";
import { SearchBox, Toolbar } from "../ui/Toolbar";
import { ViewNote } from "../ui/ViewNote";

const windows = ["1", "6", "24", "168"] as const;
type Window = (typeof windows)[number];
const windowLabels: Record<Window, string> = { "1": "Last hour", "6": "6 hours", "24": "24 hours", "168": "7 days" };

const kinds = ["all", "cluster", "node", "application", "service", "partition", "replica"] as const;
type Kind = (typeof kinds)[number];
const kindLabels: Record<Kind, string> = {
    all: "All",
    cluster: "Cluster",
    node: "Nodes",
    application: "Applications",
    service: "Services",
    partition: "Partitions",
    replica: "Replicas",
};

/** Where an event's entity lives, when it has a page. */
function entityHref(kind: EntityKind, entity: string): string | undefined {
    switch (kind) {
        case "node":
            return href.node(entity);
        case "application":
            return href.app(idOf(entity));
        case "service": {
            const id = idOf(entity);
            return href.service(appIdOfService(id), id);
        }
        default:
            return undefined;
    }
}

/** Events as a timeline with day headings. */
function Timeline({ events }: { events: ClusterEvent[] }) {
    let lastDay = "";
    return (
        <>
            {events.map((e, i) => {
                const d = day(e.time);
                const heading = d !== lastDay;
                lastDay = d;
                const link = entityHref(e.entityKind, e.entity);
                return (
                    <Fragment key={i}>
                        {heading && <DayHeading>{d}</DayHeading>}
                        <TimelineRow
                            time={when(e.time).split(" ").pop()}
                            title={e.kind}
                            sub={link ? <a className="link" href={link}>{e.entity}</a> : e.entity}
                            detail={e.details || undefined}
                        />
                    </Fragment>
                );
            })}
        </>
    );
}

export function Events() {
    const [hours, setHours] = useQueryParam<Window>("hours", "1");
    const [kind, setKind] = useQueryParam<Kind>("kind", "all");
    const [search, setSearch] = useQueryParam("q", "");
    const events = useResource(`events:${hours}`, () => api.events(Number(hours)));

    return (
        <>
            <h2 className="sr-only">The cluster's events, newest first.</h2>
            <PageHeader title="Events" summary={windowLabels[hours]} onRefresh={events.reload} />
            <ViewNote />
            {/* Nothing to filter without an event store. */}
            {!events.data?.unavailable && (
                <>
                    <Toolbar>
                        <SearchBox value={search} onChange={setSearch} placeholder="Filter by event, entity or detail" />
                        <Chips label="Time window" options={windows} labels={windowLabels} value={hours} onChange={setHours} />
                    </Toolbar>
                    <Toolbar>
                        <Chips label="Entity kind" options={kinds} labels={kindLabels} value={kind} onChange={setKind} />
                    </Toolbar>
                </>
            )}
            <Load resource={events} shape="rows">
                {(d) => {
                    if (d.unavailable) return <Empty>{d.unavailable}</Empty>;
                    const q = search.toLowerCase();
                    const shown = d.events.filter((e) => (kind === "all" || e.entityKind === kind) && (!q || `${e.kind} ${e.entity} ${e.details}`.toLowerCase().includes(q)));
                    return (
                        <Section label={windowLabels[hours]} aside={shown.length === d.events.length ? plural(d.events.length, "event") : `${shown.length} of ${d.events.length}`}>
                            {shown.length === 0 && <Empty>{d.events.length === 0 ? "Nothing happened in this window." : "No events match this filter."}</Empty>}
                            <Timeline events={shown} />
                        </Section>
                    );
                }}
            </Load>
        </>
    );
}

/** An entity's own events (and those of what's under it) over the last day, for its Events tab. */
export function EntityEvents({ entity }: { entity: string }) {
    const events = useResource(`events:${entity}`, () => api.events(24, entity));
    return (
        <Load resource={events} shape="rows">
            {(d) =>
                d.unavailable ? (
                    <Empty>{d.unavailable}</Empty>
                ) : d.events.length === 0 ? (
                    <Empty>Nothing happened here in the last day.</Empty>
                ) : (
                    <Section label="Last 24 hours" aside={plural(d.events.length, "event")}>
                        <Timeline events={d.events} />
                    </Section>
                )
            }
        </Load>
    );
}
