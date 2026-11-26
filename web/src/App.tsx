import { useCallback, useEffect, useMemo, useState } from "react";
import { api } from "./lib/api";
import { NoticeContext } from "./lib/notice";
import { RefreshContext, useResource } from "./lib/resource";
import { usePath } from "./lib/router";
import { Notice } from "./ui/Notice";
import { Empty } from "./ui/Section";
import { Sidebar } from "./Sidebar";
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
import { Types } from "./views/Types";
import { ImageStore } from "./views/ImageStore";
import { Contexts } from "./views/Contexts";
import { SessionContext, useFollowSwitches } from "./lib/session";
import { OperationsContext, useOperationsPoll } from "./lib/operations";
import "./views/views.css";
import { Gallery } from "./gallery/Gallery";
import { Find } from "./ui/Find";

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
        case "types":
            return <Types />;
        case "store":
            return <ImageStore />;
        case "events":
            return <Events />;
        case "activity":
            return <Activity />;
        case "contexts":
            return <Contexts />;
        default:
            return <Empty>There's no page here. Pick one from the sidebar.</Empty>;
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
    const [finding, setFinding] = useState(false);
    const [collapsed, setCollapsed] = useState(() => localStorage.getItem("sfx-sidebar") === "collapsed");
    const collapse = (folded: boolean) => {
        setCollapsed(folded);
        if (folded) localStorage.setItem("sfx-sidebar", "collapsed");
        else localStorage.removeItem("sfx-sidebar");
    };
    const notify = useCallback((text: string, ok: boolean) => setOutcome({ text, ok }), []);
    useFollowSwitches(session, bump, notify);
    // When a change actually finishes, say so and reload what's on screen.
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

    // As in the terminal: "c" opens contexts, "/" the page's filter box, not while typing or in a dialog. ⌘K or ctrl+K
    // finds anything from anywhere (ctrl+F stays the browser's own find).
    useEffect(() => {
        const onKey = (e: KeyboardEvent) => {
            if (e.key.toLowerCase() === "k" && (e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey) {
                e.preventDefault();
                setFinding(true);
                return;
            }
            const el = e.target as HTMLElement;
            if (e.metaKey || e.ctrlKey || e.altKey || el.closest("input, textarea, select, [contenteditable], dialog")) return;
            if (e.key === "c" && !location.hash.startsWith("#/contexts")) {
                location.hash = "#/contexts";
            } else if (e.key === "/" || e.key === "c") {
                const search = document.querySelector<HTMLInputElement>("main input[type=search]");
                if (!search) return;
                e.preventDefault();
                search.focus();
                search.select();
            }
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
                <div className={`app ${collapsed ? "sidebar-collapsed" : ""}`}>
                    <TopBar session={session.data} onSessionChange={session.reload} onFind={() => setFinding(true)} contextsOpen={path[0] === "contexts"} />
                    <Sidebar session={session.data} current={path[0] ?? ""} collapsed={collapsed} onCollapse={collapse} />
                    {finding && session.data && <Find onClose={() => setFinding(false)} />}
                    <div className="main">
                        <div className="main-inner">
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
                    </div>
                </div>
            </NoticeContext.Provider>
            </OperationsContext.Provider>
            </SessionContext.Provider>
        </RefreshContext.Provider>
    );
}
