import { useState } from "react";
import { api } from "../lib/api";
import { count, plural } from "../lib/format";
import { healthFilters, matches } from "../lib/health";
import type { HealthFilter } from "../lib/health";
import { useResource } from "../lib/resource";
import { href, useQueryParam } from "../lib/router";
import type { Node } from "../lib/types";
import { Link } from "../ui/Button";
import { Chips } from "../ui/Chips";
import { Load } from "../ui/Load";
import { MetricCard, Metrics } from "../ui/MetricCard";
import { NodeMap } from "../ui/NodeMap";
import { PageHeader } from "../ui/PageHeader";
import { LinkRow } from "../ui/Row";
import { Empty, GroupHeading, Section } from "../ui/Section";
import { SearchBox, Toolbar } from "../ui/Toolbar";
import { CountTab, Tabs, useTab } from "../ui/Tabs";
import { LoadView } from "./LoadView";
import { Repairs } from "./Repairs";
import { useRepairsAvailable } from "../lib/session";

const layouts = ["List", "Map"] as const;
type Layout = (typeof layouts)[number];

export function nodeFacts(n: Node) {
    return [n.status, n.nodeType, `UD ${n.upgradeDomain}`, n.faultDomain, n.isSeed ? "seed" : null].filter(Boolean).join(" · ");
}

export function Nodes() {
    const nodes = useResource("nodes", api.nodes);
    const [filter, setFilter] = useQueryParam<HealthFilter>("health", "All");
    const [layout, setLayout] = useQueryParam<Layout>("layout", "List");
    const [search, setSearch] = useQueryParam("q", "");
    const [selected, setSelected] = useState<string | null>(null);
    const [tab, setTab] = useTab(["nodes", "load", "repairs"] as const);
    const repairsAvailable = useRepairsAvailable();

    return (
        <Load resource={nodes}>
            {(all) => {
                const q = search.toLowerCase();
                const shown = all.filter((n) => matches(filter, n.health) && (!q || `${n.name} ${n.nodeType} ${n.address}`.toLowerCase().includes(q)));
                const types = [...new Set(shown.map((n) => n.nodeType))].sort();
                const picked = all.find((n) => n.name === selected);
                return (
                    <>
                        <h2 className="sr-only">
                            {plural(all.length, "node")}, {count(all, (n) => n.health === "Warning" || n.health === "Error")} unhealthy.
                        </h2>
                        <PageHeader
                            title="Nodes"
                            summary={`${plural(all.length, "node")} · ${plural(new Set(all.map((n) => n.nodeType)).size, "node type")}`}
                            onRefresh={nodes.reload}
                            updated={nodes.at}
                        />
                        <Tabs
                            value={tab}
                            onChange={setTab}
                            tabs={[
                                { id: "nodes", label: <CountTab label="Nodes" count={all.length} /> },
                                { id: "load", label: "Load" },
                                { id: "repairs", label: "Repairs", hidden: !repairsAvailable },
                            ]}
                        >
                        {tab === "load" && <LoadView />}
                        {tab === "repairs" && repairsAvailable && <Repairs />}
                        {tab === "nodes" && (
                        <>
                        <div className="section">
                        <Metrics>
                            <MetricCard label="Up" value={count(all, (n) => n.status === "Up")} />
                            <MetricCard label="Down" value={count(all, (n) => n.status === "Down")} />
                            <MetricCard label="Disabled" value={count(all, (n) => n.status.startsWith("Disabl"))} />
                            <MetricCard label="Seed nodes" value={count(all, (n) => n.isSeed)} />
                        </Metrics>
                        </div>
                        <Toolbar>
                            <SearchBox value={search} onChange={setSearch} placeholder="Filter by name, type or address" />
                            <Chips label="Filter by health" options={healthFilters} value={filter} onChange={setFilter} />
                            <Chips label="Layout" options={layouts} value={layout} onChange={setLayout} />
                        </Toolbar>
                        <Section label={layout === "Map" ? "By node type and upgrade domain" : "All nodes"} aside={shown.length === all.length ? plural(all.length, "node") : `${shown.length} of ${all.length}`}>
                            {shown.length === 0 && <Empty>No nodes match this filter.</Empty>}
                            {layout === "List" &&
                                types.map((type) => {
                                    const group = shown.filter((n) => n.nodeType === type);
                                    return (
                                        <div key={type}>
                                            <GroupHeading label={type} aside={plural(group.length, "node")} />
                                            {group.map((n) => (
                                                <LinkRow key={n.name} href={href.node(n.name)} icon="server" name={n.name} sub={nodeFacts(n)} reason={n.reason} status={n.health} />
                                            ))}
                                        </div>
                                    );
                                })}
                            {layout === "Map" && shown.length > 0 && (
                                <>
                                    <NodeMap nodes={shown} selected={selected} onSelect={setSelected} />
                                    <p className="map-detail" aria-live="polite">
                                        {picked ? (
                                            <>
                                                <Link href={href.node(picked.name)}>{picked.name}</Link>
                                                <span className="secondary">
                                                    {" "}
                                                    · {nodeFacts(picked)} · {picked.health}
                                                </span>
                                            </>
                                        ) : (
                                            <span className="muted">Select a node to see its details.</span>
                                        )}
                                    </p>
                                </>
                            )}
                        </Section>
                        </>
                        )}
                        </Tabs>
                    </>
                );
            }}
        </Load>
    );
}
