import { useEffect, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import { api } from "../lib/api";
import { href } from "../lib/router";
import type { SearchHit } from "../lib/types";
import { Icon } from "./Icon";
import { useModal } from "./Overlay";
import "./Find.css";

const icons: Record<SearchHit["kind"], string> = { node: "server", application: "apps", service: "box", partition: "layout-grid" };
const kinds: Record<SearchHit["kind"], string> = { node: "Node", application: "Application", service: "Service", partition: "Partition" };

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
    const list = useRef<HTMLUListElement>(null);

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

    return (
        <dialog {...props} className="dialog find" aria-label="Find">
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
            </label>
            {state === "failed" && <p className="find-empty text-danger">{error}</p>}
            {state === "done" && hits.length === 0 && <p className="find-empty">Nothing matches “{query.trim()}”.</p>}
            {state === "idle" && <p className="find-empty">Names, types and addresses; paste a partition id to jump to it.</p>}
            {hits.length > 0 && (
                <ul id="find-results" className="find-results" role="listbox" ref={list}>
                    {hits.map((hit, i) => (
                        <li
                            key={`${hit.kind}:${hit.title}:${hit.partitionId ?? ""}`}
                            id={`find-${i}`}
                            data-index={i}
                            role="option"
                            aria-selected={i === at}
                            className="find-hit"
                            onMouseMove={() => setAt(i)}
                            onClick={() => open(hit)}
                        >
                            <Icon name={icons[hit.kind]} className="find-hit-icon" />
                            <span className="find-hit-main">
                                <span className="find-hit-title">{hit.title}</span>
                                {hit.sub && <span className="find-hit-sub">{hit.sub}</span>}
                            </span>
                            <span className="find-hit-kind">{kinds[hit.kind]}</span>
                        </li>
                    ))}
                </ul>
            )}
            <p className="find-keys">
                <kbd>↑</kbd>
                <kbd>↓</kbd> move · <kbd>Enter</kbd> open · <kbd>Esc</kbd> close
            </p>
        </dialog>
    );
}
