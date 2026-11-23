import { api } from "../lib/api";
import { duration, plural, when } from "../lib/format";
import { useResource } from "../lib/resource";
import { useOperations } from "../lib/operations";
import { Load } from "../ui/Load";
import { PageHeader } from "../ui/PageHeader";
import { Empty, Section } from "../ui/Section";
import { TimelineRow } from "../ui/Timeline";
import { ProgressBar } from "../ui/Meter";

export function Activity() {
    const activity = useResource("activity", api.activity);
    const operations = useOperations();
    return (
        <Load resource={activity}>
            {(entries) => (
                <>
                    <h2 className="sr-only">The last {entries.length} changes made through sfx.</h2>
                    <PageHeader title="Activity" summary={plural(entries.length, "change")} onRefresh={activity.reload} />
                    {operations.length > 0 && (
                        <Section label="Since connecting" aside={`${operations.filter((o) => o.state === "running").length} running`}>
                            {operations.map((o) => (
                                <TimelineRow
                                    key={o.id}
                                    time={when(o.started)}
                                    title={o.title}
                                    sub={o.finished ? `Took ${duration((new Date(o.finished).getTime() - new Date(o.started).getTime()) / 1000)}` : "Following it until the cluster is done"}
                                    detail={
                                        o.state === "running" && o.progress != null ? (
                                            <>
                                                {o.detail}
                                                <ProgressBar pct={Math.round(o.progress * 100)} label={`${o.title}: ${Math.round(o.progress * 100)}%`} />
                                            </>
                                        ) : (
                                            o.detail
                                        )
                                    }
                                    detailTone={o.state === "failed" ? "danger" : undefined}
                                    status={o.state === "running" ? "In progress" : o.state === "done" ? "Done" : "Failed"}
                                />
                            ))}
                        </Section>
                    )}
                    <Section label="History" aside={plural(entries.length, "change")}>
                        {entries.length === 0 && <Empty>Nothing has been changed through sfx yet.</Empty>}
                        {entries.map((e, i) => (
                            <TimelineRow
                                key={i}
                                time={when(e.time)}
                                title={e.action}
                                sub={`${e.user} · ${e.context}`}
                                detail={e.succeeded ? undefined : e.outcome}
                                detailTone="danger"
                                status={e.succeeded ? "Done" : "Failed"}
                            />
                        ))}
                    </Section>
                </>
            )}
        </Load>
    );
}
