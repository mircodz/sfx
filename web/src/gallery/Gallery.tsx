import { useState } from "react";
import type { ReactNode } from "react";
import { ActionGroup } from "../actions/ActionGroup";
import { ConfirmAction } from "../actions/ConfirmAction";
import { ApplicationEditor } from "../actions/editors/ApplicationEditor";
import { ScaleEditor } from "../actions/editors/ScaleEditor";
import type { Status } from "../lib/types";
import { Button, IconButton, Link } from "../ui/Button";
import { Chips } from "../ui/Chips";
import { Disclosure } from "../ui/Disclosure";
import { DomainStrip } from "../ui/DomainStrip";
import { Fields } from "../ui/Fields";
import { Checkbox, ChoiceField, Field } from "../ui/Form";
import { ErrorsAndWarnings, Evaluations, HealthPanel } from "../ui/Health";
import { Load, Skeleton } from "../ui/Load";
import { Menu } from "../ui/Menu";
import { Meters, ProgressBar } from "../ui/Meter";
import { MetricCard, Metrics } from "../ui/MetricCard";
import { NodeMap } from "../ui/NodeMap";
import { Notice } from "../ui/Notice";
import { PageHeader } from "../ui/PageHeader";
import { Pill } from "../ui/Pill";
import { DisclosureRow, LinkRow, RowActions } from "../ui/Row";
import { Empty, GroupHeading, Section } from "../ui/Section";
import { CountTab, HealthTab, Tabs } from "../ui/Tabs";
import { DayHeading, TimelineRow } from "../ui/Timeline";
import { SearchBox, Toolbar } from "../ui/Toolbar";
import { UpgradeCard } from "../views/UpgradeCard";
import { Code } from "../ui/Document";
import * as f from "./fixtures";
import "./Gallery.css";

const statuses: Status[] = ["Healthy", "Warning", "Error", "Unknown", "Done", "Upgrading", "Verifying", "Pending", "Failed"];

/** Labelled, so a change to a component is seen everywhere it's used. */
function Specimen({ name, children }: { name: string; children: ReactNode }) {
    return (
        <section className="specimen" id={name.toLowerCase().replace(/\W+/g, "-")}>
            <h2 className="specimen-name">{name}</h2>
            <div className="specimen-body">{children}</div>
        </section>
    );
}

/** Every component in every state, from fixtures, so no cluster is needed; scripts/check-gallery.ts screenshots it. */
export function Gallery() {
    const [chip, setChip] = useState<"All" | "Healthy" | "Warning" | "Error">("All");
    const [tab, setTab] = useState<"overview" | "health" | "events">("overview");
    const [open, setOpen] = useState(true);
    const [search, setSearch] = useState("");
    const [choice, setChoice] = useState("Pause");
    const [overlay, setOverlay] = useState<string | null>(null);
    const close = () => setOverlay(null);

    return (
        <div className="gallery">
            <PageHeader title="Component gallery" summary="Every component in every state, from made-up data." />

            <Specimen name="Pills">
                <div className="cluster">
                    {statuses.map((s) => (
                        <Pill key={s} status={s} />
                    ))}
                </div>
            </Specimen>

            <Specimen name="Buttons">
                <div className="cluster">
                    <Button>Default</Button>
                    <Button variant="primary">Primary</Button>
                    <Button variant="danger">Danger</Button>
                    <Button variant="danger-filled">Danger, filled</Button>
                    <Button disabled>Disabled</Button>
                    <IconButton icon="refresh" label="Refresh" />
                    <IconButton icon="dots" label="More actions" bordered />
                    <Link href="#/gallery">A link</Link>
                </div>
            </Specimen>

            <Specimen name="Chips">
                <Chips label="Health" options={["All", "Healthy", "Warning", "Error"] as const} value={chip} onChange={setChip} />
            </Specimen>

            <Specimen name="Page header">
                <PageHeader
                    title="fabric:/orders/Counter"
                    summary="Stateful · CounterType · Active · updated 14:02:31"
                    crumbs={[
                        { label: "Applications", href: "#/gallery" },
                        { label: "fabric:/a-very-long-application-name-that-should-truncate-nicely", href: "#/gallery/x" },
                    ]}
                    status="Warning"
                    onRefresh={() => {}}
                    actions={<ActionGroup actions={[f.actions.scale, f.actions.deactivate, f.actions.restart]} />}
                />
            </Specimen>

            <Specimen name="Tabs">
                <Tabs
                    value={tab}
                    onChange={setTab}
                    tabs={[
                        { id: "overview", label: <CountTab label="Replicas" count={3} /> },
                        { id: "health", label: <HealthTab health="Error" /> },
                        { id: "events", label: "Events" },
                    ]}
                >
                    <Empty>The {tab} tab's content.</Empty>
                </Tabs>
            </Specimen>

            <Specimen name="Metric cards">
                <Metrics>
                    <MetricCard label="Nodes" value={5} note="1 down · 1 disabled" href="#/gallery" />
                    <MetricCard label="Applications" value={17} note="3 unhealthy" />
                    <MetricCard label="Upgrades running" value={0} />
                    <MetricCard label="Events" value="–" note="last hour" />
                </Metrics>
            </Specimen>

            <Specimen name="Rows">
                <Toolbar>
                    <SearchBox value={search} onChange={setSearch} placeholder="Filter by name" />
                    <Chips label="Health" options={["All", "Healthy", "Warning", "Error"] as const} value={chip} onChange={setChip} />
                </Toolbar>
                <Section label="Link rows" aside="4 nodes">
                    <GroupHeading label="frontend" aside="3 nodes" />
                    <LinkRow href="#/gallery" icon="server" name="_Node_0" sub="Up · frontend · UD 0 · fd:/0 · seed" status="Healthy" />
                    <LinkRow href="#/gallery" icon="server" name="_Node_2" sub="Down · frontend · UD 2" reason="Node went down at 13:58 and has not come back." status="Error" />
                    <LinkRow href="#/gallery" icon="apps" name="fabric:/a-very-long-application-name-that-goes-on-and-on-and-on-and-on" sub="DemoAppType 1.0.0 · Ready" reason="Memory use at 91% for 15 minutes." status="Warning" value="3 of 3" />
                    <LinkRow href="#/gallery" icon="layout-grid" name={<span className="mono">7be2a1c4-0000-0000-0000-000000000000</span>} sub="Int64Range · Ready" status="Unknown" />
                </Section>
                <Section label="Disclosure rows">
                    <DisclosureRow id="open" icon="crown" name="Primary on _Node_1" sub="Ready · id 134395751766606130" status="Healthy" open={open} onToggle={() => setOpen(!open)}>
                        <Fields
                            fields={[
                                { name: "Role", value: "Primary" },
                                { name: "Node", value: "_Node_1", href: "#/gallery" },
                                { name: "Address", value: null },
                            ]}
                        />
                        <RowActions>
                            <ActionGroup actions={[f.actions.move, f.actions.remove]} />
                        </RowActions>
                    </DisclosureRow>
                    <DisclosureRow id="closed" icon="copy" name="ActiveSecondary on _Node_2" sub="Down · id 134395788921299710" status="Error" open={false} onToggle={() => {}}>
                        <></>
                    </DisclosureRow>
                </Section>
                <Empty>No nodes match this filter.</Empty>
            </Specimen>

            <Specimen name="Fields">
                <Fields
                    mono
                    fields={f.application.parameters.map((p) => ({ name: p.name, value: p.value, note: p.overridden ? `default ${p.default || "empty"}` : "default" }))}
                />
            </Specimen>

            <Specimen name="Meters">
                <ProgressBar pct={40} label="40%" />
                <div className="specimen-gap" />
                <Meters metrics={f.metrics} />
                <Meters metrics={[]} />
            </Specimen>

            <Specimen name="Errors and warnings">
                <ErrorsAndWarnings evaluations={f.evaluations} />
                <div className="specimen-gap" />
                <Evaluations evaluations={f.evaluations} />
            </Specimen>

            <Specimen name="Health panel">
                <HealthPanel health={f.health} />
                <HealthPanel health={{ health: "Healthy", evaluations: [], events: [] }} />
            </Specimen>

            <Specimen name="Timeline">
                <DayHeading>Today</DayHeading>
                <TimelineRow time="14:02" title="Restart replica 1234 on _Node_1" sub="mirco · azure-dev" status="Done" />
                <TimelineRow
                    time="19 Nov 22:33"
                    title="Upgrade fabric:/users to 1.0.0"
                    sub="mirco · azure-dev"
                    detail="Frontend_InstanceCount sets the InstanceCount of the default service Frontend, and this cluster doesn't let upgrades change default services."
                    detailTone="danger"
                    status="Failed"
                />
                <TimelineRow time="13:58" title="NodeDown" sub="_Node_2" detail="NodeInstance=1342  LastNodeUpAt=2026-11-20T13:40:00Z" />
            </Specimen>

            <Specimen name="Upgrades">
                <Section label="Running">
                    <UpgradeCard upgrade={{ ...f.upgradeRunning, actions: [f.actions.rollback] }} />
                </Section>
                <Section label="Failed">
                    <UpgradeCard upgrade={{ ...f.upgradeFailed, actions: [f.actions.resume, f.actions.rollback] }} />
                </Section>
                <Section label="Complete, domains only">
                    <DomainStrip upgrade={f.upgradeDone} />
                </Section>
            </Specimen>

            <Specimen name="Node map">
                <NodeMap nodes={f.nodes} selected="_Node_1" onSelect={() => {}} />
            </Specimen>

            <Specimen name="Documents">
                <Code text={f.xml.text} format="xml" wrap={false} />
                <div className="specimen-gap" />
                <Code text={f.json.text} format="json" wrap={false} />
            </Specimen>

            <Specimen name="Notices">
                <Notice tone="success">Restart replica 1234 on _Node_1: requested.</Notice>
                <Notice tone="danger" onDismiss={() => {}}>
                    Couldn't reach the cluster. Showing data from 14:02:31.
                </Notice>
            </Specimen>

            <Specimen name="Loading">
                <Skeleton shape="page" />
                <Load resource={{ error: "Connection refused." }}>{() => null}</Load>
            </Specimen>

            <Specimen name="Form fields">
                <Field label="Instance count" help="How many instances run; -1 runs one on every node.">
                    {(id) => <input id={id} type="number" defaultValue="3" />}
                </Field>
                <Field label="Target replica set size" changed onRevert={() => {}} help="Was 3.">
                    {(id) => <input id={id} type="number" defaultValue="5" />}
                </Field>
                <Field label="Min replica set size" error="No more than the target size.">
                    {(id) => <input id={id} type="number" defaultValue="9" />}
                </Field>
                <Field label={<span className="mono">Counter_PartitionCount</span>} help="Sets how Counter is partitioned (PartitionCount), which can't change once the service exists.">
                    {(id) => <input id={id} defaultValue="5" disabled />}
                </Field>
                <ChoiceField label="Why" choices={f.actions.deactivate.inputs[0].choices!} value={choice} onChange={setChoice} />
                <Checkbox label="Ignore placement constraints" checked onChange={() => {}} />
                <Disclosure label="Timeouts and health policy" aside="Cluster defaults">
                    <Empty>Collapsed content.</Empty>
                </Disclosure>
            </Specimen>

            <Specimen name="Menu">
                <Menu
                    label="More actions"
                    items={[
                        { id: "a", label: "Reset load", onPick: () => {} },
                        { id: "b", label: "Delete", danger: true, onPick: () => {} },
                    ]}
                />
            </Specimen>

            <Specimen name="Overlays">
                <div className="cluster">
                    <Button onClick={() => setOverlay("confirm")}>Confirm dialog</Button>
                    <Button onClick={() => setOverlay("destructive")}>Destructive dialog</Button>
                    <Button onClick={() => setOverlay("inputs")}>Dialog with inputs</Button>
                    <Button onClick={() => setOverlay("scale")}>Scale panel</Button>
                    <Button onClick={() => setOverlay("application")}>Application panel</Button>
                </div>
                {overlay === "confirm" && <ConfirmAction action={f.actions.rollback} onClose={close} />}
                {overlay === "destructive" && <ConfirmAction action={f.actions.restart} onClose={close} />}
                {overlay === "inputs" && <ConfirmAction action={f.actions.deactivate} onClose={close} />}
                {overlay === "scale" && <ScaleEditor action={f.actions.scale} onClose={close} />}
                {overlay === "application" && <ApplicationEditor action={f.upgradeAction} app={f.application} onClose={close} />}
            </Specimen>
        </div>
    );
}
