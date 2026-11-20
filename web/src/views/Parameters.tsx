import { useState } from "react";
import { plural } from "../lib/format";
import type { Parameter } from "../lib/types";
import { Chips } from "../ui/Chips";
import { Fields } from "../ui/Fields";
import { Empty, Section } from "../ui/Section";
import { SearchBox, Toolbar } from "../ui/Toolbar";

const shows = ["Set", "All"] as const;
type Show = (typeof shows)[number];

/** Sets them first: what's set on the application is what's usually being looked for. */
export function setFirst(a: Parameter, b: Parameter) {
    return Number(b.overridden) - Number(a.overridden) || a.name.localeCompare(b.name);
}

/**
 * An application's parameters, which can run into the thousands: what's set on the application by default, everything
 * a search away. Defaults are what the application type declares; they aren't interesting until they're changed.
 */
export function Parameters({ parameters }: { parameters: Parameter[] }) {
    const [search, setSearch] = useState("");
    const anySet = parameters.some((p) => p.overridden);
    const [show, setShow] = useState<Show>(anySet ? "Set" : "All");
    const q = search.toLowerCase();
    const shown = parameters
        .filter((p) => (show === "All" || p.overridden) && (!q || `${p.name} ${p.value}`.toLowerCase().includes(q)))
        .sort(setFirst);
    const set = parameters.filter((p) => p.overridden).length;

    return (
        <Section label="Parameters" aside={parameters.length > 0 ? `${set} of ${plural(parameters.length, "parameter")} set` : undefined}>
            {parameters.length === 0 ? (
                <Empty>No parameters.</Empty>
            ) : (
                <>
                    <Toolbar>
                        <SearchBox value={search} onChange={setSearch} placeholder={`Find among ${plural(parameters.length, "parameter")}`} />
                        <Chips label="Show" options={shows} value={show} onChange={setShow} labels={{ Set: `Set (${set})`, All: `All (${parameters.length})` }} />
                    </Toolbar>
                    {shown.length === 0 && <Empty>{show === "Set" && !q ? "Nothing is set on the application: every parameter has its default." : "No parameters match."}</Empty>}
                    <Fields
                        mono
                        fields={shown.map((p) => ({
                            name: p.name,
                            value: p.value,
                            note: p.overridden ? (p.default !== undefined && p.default !== p.value ? `default ${p.default || "empty"}` : undefined) : "default",
                        }))}
                    />
                </>
            )}
        </Section>
    );
}
