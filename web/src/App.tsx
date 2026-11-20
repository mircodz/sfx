import { useCallback, useEffect, useMemo, useState } from "react";
import { api } from "./lib/api";
import { NoticeContext } from "./lib/notice";
import { RefreshContext, useResource } from "./lib/resource";
import { usePath } from "./lib/router";
import { Notice } from "./ui/Notice";
import { Empty } from "./ui/Section";
import { TopBar } from "./TopBar";
import { Activity } from "./views/Activity";
import { ApplicationDetail } from "./views/ApplicationDetail";
import { Applications } from "./views/Applications";
import { Events } from "./views/Events";
import { NodeDetail } from "./views/NodeDetail";
import { Nodes } from "./views/Nodes";
import { Overview } from "./views/Overview";
import { PartitionDetail } from "./views/PartitionDetail";
import { Replicas } from "./views/Replicas";
import { ServiceDetail } from "./views/ServiceDetail";
import { Upgrades } from "./views/Upgrades";
import { Contexts } from "./views/Contexts";
import { SessionContext } from "./lib/session";
import { OperationsContext, useOperationsPoll } from "./lib/operations";
import "./views/views.css";
import { Gallery } from "./gallery/Gallery";

/** #/nodes/:name, #/apps/:app/services/:service/partitions/:partition and the top-level pages. */
function Page({ path }: { path: string[] }) {
    const [section, a, b, c, d, e] = path;
    switch (section) {
        case undefined:
            return <Overview />;
        case "nodes":
            return a ? <NodeDetail key={a} name={a} /> : <Nodes />;
        case "apps":
            if (b === "services" && d === "partitions" && e) return <PartitionDetail key={e} appId={a} serviceId={c} partitionId={e} />;
            if (b === "services" && c) return <ServiceDetail key={c} appId={a} serviceId={c} />;
            if (a) return <ApplicationDetail key={a} appId={a} />;
            return <Applications />;
        case "replicas":
            return <Replicas />;
        case "upgrades":
            return <Upgrades />;
        case "events":
            return <Events />;
        case "activity":
            return <Activity />;
        case "contexts":
            return <Contexts />;
        default:
            return <Empty>There's no page here. Pick one from the top bar.</Empty>;
    }
}

type Outcome = { text: string; ok: boolean };

export function App() {
    const path = usePath();
    const session = useResource("session", api.session, false);
    const [generation, setGeneration] = useState(0);
    const bump = useCallback(() => setGeneration((g) => g + 1), []);
    const refresh = useMemo(() => ({ generation, bump }), [generation, bump]);
    const [outcome, setOutcome] = useState<Outcome | null>(null);
    const notify = useCallback((text: string, ok: boolean) => setOutcome({ text, ok }), []);
    // When a change actually finishes: say so, and load what's on screen again.
    const operations = useOperationsPoll(generation, (op) => {
        notify(op.state === "done" ? `${op.title}: done.` : `${op.title}: ${op.detail?.replace(/\.$/, "") ?? "failed"}.`, op.state === "done");
        bump();
    });

    useEffect(() => {
        if (session.data) document.title = `${session.data.context} · sfx`;
    }, [session.data]);

    // A switch changes the session: load it again whenever everything refreshes.
    const reloadSession = session.reload;
    useEffect(() => {
        if (generation > 0) reloadSession();
    }, [generation, reloadSession]);

    // "c" opens the contexts, as in the terminal; not while typing or in a dialog.
    useEffect(() => {
        const onKey = (e: KeyboardEvent) => {
            const el = e.target as HTMLElement;
            if (e.key !== "c" || e.metaKey || e.ctrlKey || e.altKey || el.closest("input, textarea, select, [contenteditable], dialog")) return;
            location.hash = "#/contexts";
        };
        window.addEventListener("keydown", onKey);
        return () => window.removeEventListener("keydown", onKey);
    }, []);

    useEffect(() => {
        // Failures stay until dismissed; outcomes that went well leave on their own.
        if (!outcome?.ok) return;
        const id = setTimeout(() => setOutcome(null), 8000);
        return () => clearTimeout(id);
    }, [outcome]);

    return (
        <RefreshContext.Provider value={refresh}>
            <SessionContext.Provider value={session.data}>
            <OperationsContext.Provider value={operations}>
            <NoticeContext.Provider value={notify}>
                <div className="shell">
                    <TopBar session={session.data} current={path[0] ?? ""} onSessionChange={session.reload} />
                    {outcome && (
                        <Notice tone={outcome.ok ? "success" : "danger"} onDismiss={() => setOutcome(null)}>
                            {outcome.text}
                        </Notice>
                    )}
                    <main className="view">
                        {/* The gallery runs on fixtures, so it works without a cluster (and without the token). */}
                        {path[0] === "gallery" ? <Gallery /> : session.data ? <Page path={path} /> : session.error ? <Notice tone="danger">{session.error}</Notice> : null}
                    </main>
                </div>
            </NoticeContext.Provider>
            </OperationsContext.Provider>
            </SessionContext.Provider>
        </RefreshContext.Provider>
    );
}
