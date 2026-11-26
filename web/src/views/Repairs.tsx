import { api } from "../lib/api";
import { plural, when } from "../lib/format";
import { useResource } from "../lib/resource";
import { href } from "../lib/router";
import { Load } from "../ui/Load";
import { Empty, Section } from "../ui/Section";
import { TimelineRow } from "../ui/Timeline";

const active = (state: string) => !["Completed", "Invalid"].includes(state);

/** Reboots, patching and other infrastructure work on nodes. */
export function Repairs() {
    const repairs = useResource("repairs", api.repairs);
    return (
        <Load resource={repairs} shape="rows">
            {(d) => {
                if (d.unavailable) return <Empty>{d.unavailable}</Empty>;
                const running = d.repairs.filter((r) => active(r.state));
                const done = d.repairs.filter((r) => !active(r.state));
                const row = (r: (typeof d.repairs)[number]) => (
                    <TimelineRow
                        key={r.id}
                        time={r.created ? when(r.created) : undefined}
                        title={`${r.action} · ${r.id}`}
                        sub={
                            <>
                                {r.executor && `${r.executor} · `}
                                {r.nodes.map((n, i) => (
                                    <span key={n}>
                                        {i > 0 && ", "}
                                        <a className="link" href={href.node(n)}>
                                            {n}
                                        </a>
                                    </span>
                                ))}
                            </>
                        }
                        detail={r.description || undefined}
                        status={active(r.state) ? "In progress" : r.result === "Cancelled" || r.result === "Interrupted" ? "Cancelled" : r.result && r.result !== "Succeeded" ? "Failed" : "Done"}
                    />
                );
                return (
                    <>
                        <Section label="Active" aside={plural(running.length, "repair")}>
                            {running.length === 0 ? <Empty>No repair is running.</Empty> : running.map(row)}
                        </Section>
                        <Section label="Finished" aside={plural(done.length, "repair")}>
                            {done.length === 0 ? <Empty>No repairs yet.</Empty> : done.map(row)}
                        </Section>
                    </>
                );
            }}
        </Load>
    );
}
