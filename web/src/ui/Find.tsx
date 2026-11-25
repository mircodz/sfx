import { useEffect, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import { api } from "../lib/api";
import { href } from "../lib/router";
import type { SearchHit } from "../lib/types";
import { Icon } from "./Icon";
import { useModal } from "./Overlay";
import "./Find.css";

const icons: Record<SearchHit["kind"], string> = { node: "server", application: "apps", service: "box", partition: "layout-grid" };
const plurals: Record<SearchHit["kind"], string> = { node: "Nodes", application: "Applications", service: "Services", partition: "Partitions" };

/** Where a hit opens: its own page, at its place under its application and service. */
export function hitHref(hit: SearchHit): string | null {
    if (hit.kind === "node" && hit.node) return href.node(hit.node);
    if (hit.kind === "application" && hit.applicationId) return href.app(hit.applicationId);
    if (hit.kind === "service" && hit.applicationId && hit.serviceId) return href.service(hit.applicationId, hit.serviceId);
    if (hit.kind === "partition" && hit.applicationId && hit.serviceId && hit.partitionId) return href.partition(hit.applicationId, hit.serviceId, hit.partitionId);
    return null;
}

/**
 * Find anything on the cluster by name, type or address, or a partition by its id; opens it where it lives.
 * Typing searches on the server after a short pause; arrows move, Enter opens, Esc closes.
 */
export function Find({ onClose }: { onClose: () => void }) {
    const { props, close } = useModal(onClose);
    const [query, setQuery] = useState("");
    const [hits, setHits] = useState<SearchHit[]>([]);
    const [state, setState] = useState<"idle" | "searching" | "done" | "failed">("idle");
    const [error, setError] = useState("");
    const [at, setAt] = useState(0);
    const list = useRef<HTMLDivElement>(null);

    useEffect(() => {
        const q = query.trim();
        if (!q) {
            setHits([]);
            setState("idle");
            return;
        }
        let live = true;
        setState("searching");
        const id = setTimeout(() => {
            api.search(q)
                .then((found) => {
                    if (!live) return;
                    setHits(found);
                    setAt(0);
                    setState("done");
                })
                .catch((e: unknown) => {
                    if (!live) return;
                    setError(e instanceof Error ? e.message : String(e));
                    setState("failed");
                });
        }, 150);
        return () => {
            live = false;
            clearTimeout(id);
        };
    }, [query]);

    useEffect(() => {
        list.current?.querySelector<HTMLElement>(`[data-index="${at}"]`)?.scrollIntoView({ block: "nearest" });
    }, [at]);

    const open = (hit: SearchHit | undefined) => {
        const to = hit && hitHref(hit);
        if (!to) return;
        location.hash = to;
        close();
    };

    const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
            e.preventDefault();
            if (hits.length) setAt((i) => (i + (e.key === "ArrowDown" ? 1 : hits.length - 1)) % hits.length);
        } else if (e.key === "Enter") {
            e.preventDefault();
            open(hits[at]);
        }
    };

    // Grouped by kind, in the order the best hit of each kind ranked; the arrows move through all of them.
    const groups: { kind: SearchHit["kind"]; hits: { hit: SearchHit; index: number }[] }[] = [];
    hits.forEach((hit, index) => {
        const group = groups.find((g) => g.kind === hit.kind) ?? groups[groups.push({ kind: hit.kind, hits: [] }) - 1];
        group.hits.push({ hit, index });
    });

    return (
        <dialog {...props} className="find" aria-label="Find">
            <label className="find-input">
                <Icon name="search" className="find-icon" />
                <span className="sr-only">Find a node, application, service or partition id</span>
                <input
                    autoFocus
                    value={query}
                    onChange={(e) => setQuery(e.target.value)}
                    onKeyDown={onKey}
                    placeholder="Find a node, application, service or partition id"
                    spellCheck={false}
                    autoComplete="off"
                    role="combobox"
                    aria-expanded={hits.length > 0}
                    aria-controls="find-results"
                    aria-activedescendant={hits.length ? `find-${at}` : undefined}
                />
                {state === "searching" && <span className="find-busy" aria-label="Searching" />}
                <kbd>esc</kbd>
            </label>
            {state === "failed" && <p className="find-empty text-danger">{error}</p>}
            {state === "done" && hits.length === 0 && (
                <p className="find-empty">
                    Nothing matches <b>{query.trim()}</b>.
                </p>
            )}
            {state === "idle" && <p className="find-empty">Search by name, type or address, or paste a partition id to jump to it.</p>}
            {hits.length > 0 && (
                <div id="find-results" className="find-results" role="listbox" ref={list}>
                    {groups.map((g) => (
                        <div key={g.kind} role="group" aria-label={plurals[g.kind]}>
                            <div className="find-group">{plurals[g.kind]}</div>
                            {g.hits.map(({ hit, index }) => (
                                <div
                                    key={`${hit.kind}:${hit.title}:${hit.partitionId ?? ""}`}
                                    id={`find-${index}`}
                                    data-index={index}
                                    role="option"
                                    aria-selected={index === at}
                                    className="find-hit"
                                    onMouseMove={() => setAt(index)}
                                    onClick={() => open(hit)}
                                >
                                    <span className="find-hit-icon">
                                        <Icon name={icons[hit.kind]} />
                                    </span>
                                    <span className="find-hit-title">{hit.title}</span>
                                    {hit.sub && <span className="find-hit-sub">{hit.sub}</span>}
                                    <kbd className="find-hit-enter" aria-hidden="true">
                                        ↵
                                    </kbd>
                                </div>
                            ))}
                        </div>
                    ))}
                </div>
            )}
            <div className="find-foot">
                <span>
                    <kbd>↑</kbd>
                    <kbd>↓</kbd> to move
                </span>
                <span>
                    <kbd>↵</kbd> to open
                </span>
                <span>
                    <kbd>esc</kbd> to close
                </span>
            </div>
        </dialog>
    );
}
