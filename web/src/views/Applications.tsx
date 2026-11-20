import { api } from "../lib/api";
import { count, plural } from "../lib/format";
import { healthFilters, matches } from "../lib/health";
import type { HealthFilter } from "../lib/health";
import { useResource } from "../lib/resource";
import { href, useQueryParam } from "../lib/router";
import { Chips } from "../ui/Chips";
import { Load } from "../ui/Load";
import { PageHeader } from "../ui/PageHeader";
import { LinkRow } from "../ui/Row";
import { Empty, Section } from "../ui/Section";
import { SearchBox, Toolbar } from "../ui/Toolbar";

export function Applications() {
    const apps = useResource("apps", api.applications);
    const [filter, setFilter] = useQueryParam<HealthFilter>("health", "All");
    const [search, setSearch] = useQueryParam("q", "");

    return (
        <Load resource={apps}>
            {(all) => {
                const q = search.toLowerCase();
                const shown = all.filter((a) => matches(filter, a.health) && (!q || `${a.name} ${a.typeName}`.toLowerCase().includes(q)));
                const errors = count(all, (a) => a.health === "Error");
                const warnings = count(all, (a) => a.health === "Warning");
                return (
                    <>
                        <h2 className="sr-only">
                            {plural(all.length, "application")}, {errors} with errors and {warnings} with warnings.
                        </h2>
                        <PageHeader
                            title="Applications"
                            summary={`${plural(all.length, "application")} · ${errors} error · ${warnings} warning`}
                            onRefresh={apps.reload}
                            updated={apps.at}
                        />
                        <Toolbar>
                            <SearchBox value={search} onChange={setSearch} placeholder="Filter by name or type" />
                            <Chips label="Filter by health" options={healthFilters} value={filter} onChange={setFilter} />
                        </Toolbar>
                        <Section label="All applications" aside={shown.length === all.length ? plural(all.length, "application") : `${shown.length} of ${all.length}`}>
                            {shown.length === 0 && <Empty>No applications match this filter.</Empty>}
                            {shown.map((a) => (
                                <LinkRow key={a.id} href={href.app(a.id)} icon="apps" name={a.name} sub={[a.typeName, a.typeVersion, a.status].filter(Boolean).join(" · ")} reason={a.reason} status={a.health} />
                            ))}
                        </Section>
                    </>
                );
            }}
        </Load>
    );
}
