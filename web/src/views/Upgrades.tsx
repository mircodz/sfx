import { useState } from "react";
import { api } from "../lib/api";
import { plural } from "../lib/format";
import { useResource } from "../lib/resource";
import { direction, isRunning, upgradeProgress, upgradeWord } from "../lib/upgrades";
import { Load } from "../ui/Load";
import { PageHeader } from "../ui/PageHeader";
import { DisclosureRow } from "../ui/Row";
import { Empty, Section } from "../ui/Section";
import { UpgradeCard } from "./UpgradeCard";
import { Limited } from "../ui/ShowMore";

export function Upgrades() {
    const upgrades = useResource("upgrades", api.upgrades);
    const [open, setOpen] = useState<string | null | undefined>(undefined);

    return (
        <Load resource={upgrades}>
            {(all) => {
                const running = all.filter(isRunning);
                // The first running upgrade starts open; after that the user decides.
                const current = open === undefined ? (running[0]?.name ?? null) : open;
                return (
                    <>
                        <h2 className="sr-only">
                            {plural(all.length, "upgrade")}, {running.length} running.
                        </h2>
                        <PageHeader
                            title="Upgrades"
                            summary={`${plural(all.length, "upgrade")} · ${running.length} running`}
                            onRefresh={upgrades.reload}
                            updated={upgrades.at}
                        />
                        <Section label="Cluster and applications">
                            {all.length === 0 && <Empty>Nothing on this cluster has been upgraded yet.</Empty>}
                            <Limited items={all}>
                                {(u) => {
                                const p = upgradeProgress(u);
                                return (
                                    <DisclosureRow
                                        key={u.name}
                                        id={u.name}
                                        icon={u.kind === "cluster" ? "topology-star-3" : "apps"}
                                        name={u.name}
                                        sub={`${direction(u).from ? `${direction(u).from} to ` : ""}${direction(u).to} · ${p.done} of ${p.total} domains`}
                                        status={upgradeWord(u)}
                                        open={current === u.name}
                                        onToggle={() => setOpen(current === u.name ? null : u.name)}
                                    >
                                        <UpgradeCard upgrade={u} />
                                    </DisclosureRow>
                                );
                                }}
                            </Limited>
                        </Section>
                    </>
                );
            }}
        </Load>
    );
}
