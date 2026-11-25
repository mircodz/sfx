import { useState } from "react";
import { ActionGroup } from "../actions/ActionGroup";
import { ProvisionPanel } from "../actions/ProvisionPanel";
import { api } from "../lib/api";
import { natural, plural } from "../lib/format";
import { useResource } from "../lib/resource";
import { href, idOf, useQueryParam } from "../lib/router";
import { useSession } from "../lib/session";
import type { ApplicationType } from "../lib/types";
import { Button, Link } from "../ui/Button";
import { Fields } from "../ui/Fields";
import { Load } from "../ui/Load";
import { PageHeader } from "../ui/PageHeader";
import { DisclosureRow, RowActions } from "../ui/Row";
import { Empty, GroupHeading, Section } from "../ui/Section";
import { SearchBox, Toolbar } from "../ui/Toolbar";

export function Types() {
    const types = useResource("types", api.types);
    const session = useSession();
    const [search, setSearch] = useQueryParam("q", "");
    const [open, setOpen] = useState<string | null>(null);
    const [provisioning, setProvisioning] = useState(false);

    return (
        <Load resource={types}>
            {(all) => {
                const q = search.toLowerCase();
                const shown = all.filter((t) => !q || `${t.name} ${t.version} ${t.applications.join(" ")}`.toLowerCase().includes(q));
                const names = [...new Set(shown.map((t) => t.name))].sort(natural);
                const unused = all.filter((t) => t.applications.length === 0).length;
                return (
                    <>
                        <h2 className="sr-only">
                            {plural(new Set(all.map((t) => t.name)).size, "application type")}, {plural(all.length, "version")} provisioned.
                        </h2>
                        <PageHeader
                            title="Application types"
                            summary={[plural(new Set(all.map((t) => t.name)).size, "type"), plural(all.length, "version"), unused ? `${unused} unused` : null].filter(Boolean).join(" · ")}
                            onRefresh={types.reload}
                            updated={types.at}
                            actions={
                                !session?.readOnly && (
                                    <Button variant="primary" onClick={() => setProvisioning(true)}>
                                        Provision a package
                                    </Button>
                                )
                            }
                        />
                        <Toolbar>
                            <SearchBox value={search} onChange={setSearch} placeholder="Filter by type, version or application" />
                        </Toolbar>
                        <Section label="Provisioned" aside={plural(shown.length, "version")}>
                            {all.length === 0 && <Empty>Nothing is provisioned yet. Provision a package to create applications from it.</Empty>}
                            {all.length > 0 && shown.length === 0 && <Empty>No type matches this filter.</Empty>}
                            {names.map((name) => {
                                const versions = shown.filter((t) => t.name === name).sort((a, b) => natural(b.version, a.version));
                                return (
                                    <div key={name}>
                                        <GroupHeading label={name} aside={plural(versions.length, "version")} />
                                        {versions.map((t) => {
                                            const id = `${t.name}@${t.version}`;
                                            return (
                                                <DisclosureRow
                                                    key={id}
                                                    id={id}
                                                    icon="package"
                                                    name={t.version}
                                                    sub={[t.status, t.applications.length ? plural(t.applications.length, "application") : "No applications", plural(Object.keys(t.defaultParameters).length, "parameter")].join(" · ")}
                                                    reason={t.status !== "Available" ? t.statusDetails : undefined}
                                                    status={t.status === "Failed" ? "Error" : undefined}
                                                    open={open === id}
                                                    onToggle={() => setOpen((o) => (o === id ? null : id))}
                                                >
                                                    <TypeDetails type={t} />
                                                </DisclosureRow>
                                            );
                                        })}
                                    </div>
                                );
                            })}
                        </Section>
                        {provisioning && <ProvisionPanel onClose={() => setProvisioning(false)} />}
                    </>
                );
            }}
        </Load>
    );
}

/** Service types load on open. */
function TypeDetails({ type: t }: { type: ApplicationType }) {
    const services = useResource(`service-types:${t.name}:${t.version}`, () => api.serviceTypes(t.name, t.version), false);
    const parameters = Object.entries(t.defaultParameters).sort(([a], [b]) => natural(a, b));
    return (
        <>
            {t.applications.length > 0 ? (
                <p className="link-list">
                    <span className="secondary">Used by</span>
                    {t.applications.map((a) => (
                        <Link key={a} href={href.app(idOf(a))}>
                            {a}
                        </Link>
                    ))}
                </p>
            ) : (
                <p className="secondary">No application uses this version.</p>
            )}
            <Fields
                fields={[
                    {
                        name: "Service types",
                        value: services.data ? (services.data.length ? services.data.map((s) => `${s.name} (${s.stateful ? "stateful" : "stateless"})`).join(", ") : null) : (services.error ?? "Loading…"),
                    },
                ]}
            />
            {parameters.length > 0 && <Fields mono fields={parameters.map(([name, value]) => ({ name, value: value || null }))} />}
            {t.actions.length > 0 && (
                <RowActions>
                    <ActionGroup actions={t.actions} label={`Actions for ${t.name} ${t.version}`} />
                </RowActions>
            )}
        </>
    );
}
