import { useState } from "react";
import { ActionGroup } from "../actions/ActionGroup";
import { api } from "../lib/api";
import { plural } from "../lib/format";
import { useResource } from "../lib/resource";
import { Fields } from "../ui/Fields";
import { Load } from "../ui/Load";
import { DisclosureRow, RowActions } from "../ui/Row";
import { Empty, GroupHeading, Section } from "../ui/Section";

/** Each application's service packages on a node and their code packages (processes). */
export function CodePackages({ node }: { node: string }) {
    const packages = useResource(`packages:${node}`, () => api.packages(node));
    const [open, setOpen] = useState<string | null>(null);
    return (
        <Load resource={packages} shape="rows">
            {(list) => {
                const apps = [...new Set(list.map((p) => p.application))].sort();
                const count = list.reduce((n, p) => n + p.codePackages.length, 0);
                return (
                    <Section label="Deployed" aside={plural(count, "code package")}>
                        {list.length === 0 && <Empty>Nothing is deployed on this node.</Empty>}
                        {apps.map((app) => (
                            <div key={app}>
                                <GroupHeading label={app} />
                                {list
                                    .filter((p) => p.application === app)
                                    .flatMap((p) =>
                                        p.codePackages.map((c) => {
                                            const id = `${p.application}:${p.servicePackage}:${p.activationId}:${c.name}`;
                                            // Past failures stay in the counters forever; only one that isn't running is a problem now.
                                            const failing = !["Active", "Upgrading", "Activating"].includes(c.status) || (c.status === "Activating" && c.activationFailures > 0);
                                            return (
                                                <DisclosureRow
                                                    key={id}
                                                    id={id}
                                                    icon="terminal-2"
                                                    name={`${p.servicePackage} · ${c.name}`}
                                                    sub={`${c.status} · ${c.hostType} · version ${c.version}${c.processId ? ` · process ${c.processId}` : ""}`}
                                                    reason={failing ? `${c.status}${c.lastExitCode && c.lastExitCode !== "0" ? `, last exit code ${c.lastExitCode}` : ""}; ${plural(c.activationFailures, "failed start")} so far.` : undefined}
                                                    status={failing ? "Warning" : undefined}
                                                    open={open === id}
                                                    onToggle={() => setOpen((o) => (o === id ? null : id))}
                                                >
                                                    <Fields
                                                        fields={[
                                                            { name: "Status", value: c.status },
                                                            { name: "Host", value: c.hostType },
                                                            { name: "Process", value: c.processId },
                                                            { name: "Last exit code", value: c.lastExitCode },
                                                            { name: "Starts", value: String(c.activations) },
                                                            { name: "Failed starts", value: String(c.activationFailures) },
                                                            { name: "Service package", value: `${p.servicePackage} ${p.version}` },
                                                            { name: "Activation", value: p.activationId || "Shared" },
                                                        ]}
                                                    />
                                                    {c.actions.length > 0 && (
                                                        <RowActions>
                                                            <ActionGroup actions={c.actions} label={`Actions for ${c.name}`} />
                                                        </RowActions>
                                                    )}
                                                </DisclosureRow>
                                            );
                                        }),
                                    )}
                            </div>
                        ))}
                    </Section>
                );
            }}
        </Load>
    );
}
