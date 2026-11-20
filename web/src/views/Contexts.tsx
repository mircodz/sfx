import { useContext, useEffect, useRef, useState } from "react";
import { api } from "../lib/api";
import { plural } from "../lib/format";
import { useNotice } from "../lib/notice";
import { recentContexts, rememberContext } from "../lib/recent";
import { RefreshContext, useResource } from "../lib/resource";
import { useQueryParam } from "../lib/router";
import type { ClusterContext, Contexts as ContextsData, Folder } from "../lib/types";
import { Button } from "../ui/Button";
import { ContextLabel } from "../ui/ContextLabel";
import { Icon } from "../ui/Icon";
import { Load } from "../ui/Load";
import { Notice } from "../ui/Notice";
import { PageHeader } from "../ui/PageHeader";
import { Empty, Section } from "../ui/Section";
import { SearchBox, Toolbar } from "../ui/Toolbar";
import "./Contexts.css";

/** "prod/europe" as the reader sees it. */
export const folderLabel = (path: string) => path.split("/").join(" / ");

/** A folder's settings with what it inherits: the nearest label and color, and read-only from any parent. */
function inherited(path: string, folders: Map<string, Folder>): Folder {
    const chain = path.split("/").map((_, i, parts) => folders.get(parts.slice(0, i + 1).join("/"))).filter((f): f is Folder => !!f);
    const nearest = [...chain].reverse().find((f) => f.label);
    return { path, label: nearest?.label, color: nearest?.color, readOnly: chain.some((f) => f.readOnly) };
}

function matches(c: ClusterContext, q: string) {
    return !q || `${c.name} ${c.folder ?? ""} ${c.endpoint} ${c.label ?? ""}`.toLowerCase().includes(q);
}

/**
 * Every saved cluster, by folder, searchable: with a hundred clusters a menu doesn't do. Picking one connects in
 * the background (showing sign-in prompts here) and, once connected, opens its overview.
 */
export function Contexts() {
    const contexts = useResource("contexts", api.contexts, false);
    const [search, setSearch] = useQueryParam("q", "");
    const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
    const { bump } = useContext(RefreshContext);
    const notify = useNotice();
    const target = useRef<string | null>(null);
    const connecting = contexts.data?.connecting;

    // While connecting, ask every second how it's going; stop once it's done or failed.
    useEffect(() => {
        if (!connecting || connecting.error) return;
        const id = setInterval(contexts.reload, 1000);
        return () => clearInterval(id);
    }, [connecting, contexts.reload]);

    // Connected: remember it, refresh everything for the new cluster, and open its overview.
    useEffect(() => {
        const name = target.current;
        if (name && contexts.data && !contexts.data.connecting && contexts.data.current === name) {
            target.current = null;
            rememberContext(name);
            notify(`Switched to ${name}.`, true);
            bump();
            location.hash = "#/";
        }
    }, [contexts.data, bump, notify]);

    const connect = async (name: string) => {
        try {
            target.current = name;
            await api.connect(name);
            contexts.reload();
        } catch (e) {
            target.current = null;
            notify(e instanceof Error ? e.message : String(e), false);
        }
    };

    const dismiss = async () => {
        await api.dismissConnect();
        contexts.reload();
    };

    return (
        <Load resource={contexts}>
            {(d) => (
                <ContextList
                    data={d}
                    search={search}
                    onSearch={setSearch}
                    collapsed={collapsed}
                    onToggle={(path) =>
                        setCollapsed((s) => {
                            const next = new Set(s);
                            if (next.has(path)) next.delete(path);
                            else next.add(path);
                            return next;
                        })
                    }
                    onConnect={connect}
                    onDismiss={dismiss}
                />
            )}
        </Load>
    );
}

function ContextList({
    data,
    search,
    onSearch,
    collapsed,
    onToggle,
    onConnect,
    onDismiss,
}: {
    data: ContextsData;
    search: string;
    onSearch: (q: string) => void;
    collapsed: Set<string>;
    onToggle: (path: string) => void;
    onConnect: (name: string) => void;
    onDismiss: () => void;
}) {
    const q = search.toLowerCase();
    const shown = data.contexts.filter((c) => matches(c, q));
    const byName = new Map(data.contexts.map((c) => [c.name, c]));
    const recent = q ? [] : recentContexts().map((n) => byName.get(n)).filter((c): c is ClusterContext => !!c);
    const folders = new Map<string, Folder>(data.folders.map((f) => [f.path, f]));
    const paths = [...new Set(shown.map((c) => c.folder ?? ""))].sort((a, b) => (a === "" ? -1 : b === "" ? 1 : a.localeCompare(b)));
    const folderCount = new Set(data.contexts.map((c) => c.folder).filter(Boolean)).size;
    const busy = !!data.connecting && !data.connecting.error;

    const row = (c: ClusterContext) => (
        <ContextRow key={c.name} context={c} current={c.name === data.current} connecting={data.connecting?.context === c.name ? data.connecting : undefined} busy={busy} onConnect={onConnect} onDismiss={onDismiss} />
    );

    return (
        <>
            <h2 className="sr-only">
                {plural(data.contexts.length, "context")}; connected to {data.current}.
            </h2>
            <PageHeader
                title="Contexts"
                summary={`${plural(data.contexts.length, "context")}${folderCount ? ` in ${plural(folderCount, "folder")}` : ""}`}
            />
            <Toolbar>
                <SearchBox value={search} onChange={onSearch} placeholder="Find a context by name, folder, endpoint or label" />
            </Toolbar>

            {recent.length > 0 && <Section label="Recent">{recent.map(row)}</Section>}

            {shown.length === 0 && <Empty>No contexts match "{search}".</Empty>}
            {paths.map((path) => {
                const members = shown.filter((c) => (c.folder ?? "") === path);
                const folder = path ? inherited(path, folders) : undefined;
                const open = !!q || !collapsed.has(path);
                if (path === "") return <Section key="" label={folderCount ? "Not in a folder" : "All contexts"} aside={plural(members.length, "context")}>{members.map(row)}</Section>;
                return (
                    <section key={path} className="section">
                        <button type="button" className="folder-heading" aria-expanded={open} onClick={() => onToggle(path)}>
                            <Icon name={open ? "folder-open" : "folder"} className="folder-icon" />
                            <span className="folder-name">{folderLabel(path)}</span>
                            <ContextLabel label={folder?.label} color={folder?.color} />
                            {folder?.readOnly && <span className="pill tone-neutral">Read-only</span>}
                            <span className="spacer" />
                            <span className="secondary small">{plural(members.length, "context")}</span>
                            <Icon name="chevron-down" className="folder-chevron" />
                        </button>
                        {open && members.map(row)}
                    </section>
                );
            })}
        </>
    );
}

function ContextRow({
    context: c,
    current,
    connecting,
    busy,
    onConnect,
    onDismiss,
}: {
    context: ClusterContext;
    current: boolean;
    connecting?: ContextsData["connecting"];
    busy: boolean;
    onConnect: (name: string) => void;
    onDismiss: () => void;
}) {
    return (
        <div className={`row context-row ${current ? "current" : ""}`}>
            <button
                type="button"
                className="row-summary"
                disabled={current || busy}
                aria-current={current ? "true" : undefined}
                onClick={() => onConnect(c.name)}
                title={current ? "Connected" : `Connect to ${c.name}`}
            >
                <Icon name="server-2" className="row-icon" />
                <span className="row-main">
                    <span className="row-name">{c.name}</span>
                    <span className="row-sub">
                        {c.endpoint} · {c.auth.join(", ")}
                        {c.folder ? ` · ${folderLabel(c.folder)}` : ""}
                        {!c.saved ? " · from the command line" : ""}
                    </span>
                </span>
                <ContextLabel label={c.label} color={c.color} />
                {c.readOnly && <span className="pill tone-neutral">Read-only</span>}
                {current ? <span className="pill tone-accent">Connected</span> : connecting && !connecting.error ? <span className="pill tone-neutral">Connecting</span> : <Icon name="chevron-right" className="row-chevron" />}
            </button>
            {connecting && (
                <div className="row-details">
                    {connecting.messages.length === 0 && !connecting.error && <p className="secondary">Connecting to {c.name}…</p>}
                    {connecting.messages.map((m, i) => (
                        <SignInMessage key={i} text={m} />
                    ))}
                    {connecting.error && (
                        <Notice tone="danger" onDismiss={onDismiss}>
                            {connecting.error}
                        </Notice>
                    )}
                </div>
            )}
        </div>
    );
}

/** A sign-in prompt, e.g. "To sign in, open https://microsoft.com/devicelogin and enter the code ABCD-1234". */
function SignInMessage({ text }: { text: string }) {
    const url = text.match(/https?:\/\/\S+/)?.[0]?.replace(/[.,)]+$/, "");
    const code = text.match(/\b[A-Z0-9]{4,}(?:-[A-Z0-9]{4,})?\b(?=[^A-Z0-9]*$)/)?.[0];
    return (
        <div className="sign-in">
            <p>{text}</p>
            <div className="cluster">
                {url && (
                    <a className="btn btn-primary" href={url} target="_blank" rel="noreferrer">
                        Open sign-in page
                    </a>
                )}
                {code && <Button onClick={() => navigator.clipboard.writeText(code)}>Copy code {code}</Button>}
            </div>
        </div>
    );
}
