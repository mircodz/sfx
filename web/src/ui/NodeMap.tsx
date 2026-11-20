import { toneOf } from "../lib/health";
import type { Node } from "../lib/types";
import "./NodeMap.css";

/** Nodes as cells colored by health, grouped by node type; one can be selected. */
export function NodeMap({
    nodes,
    selected,
    onSelect,
    grouped = true,
}: {
    nodes: Node[];
    selected: string | null;
    onSelect: (name: string | null) => void;
    /** Under a heading per node type; otherwise one grid, by upgrade domain, with the type on each cell. */
    grouped?: boolean;
}) {
    if (!grouped) {
        const sorted = [...nodes].sort((a, b) => a.upgradeDomain.localeCompare(b.upgradeDomain, undefined, { numeric: true }) || a.name.localeCompare(b.name));
        return <div className="node-map">{sorted.map((n) => cell(n, selected, onSelect, n.nodeType))}</div>;
    }
    const types = [...new Set(nodes.map((n) => n.nodeType))].sort();
    return (
        <>
            {types.map((type) => (
                <div key={type} className="node-map-group">
                    <h3 className="group-heading">
                        <span>{type}</span>
                    </h3>
                    <div className="node-map">
                        {nodes
                            .filter((n) => n.nodeType === type)
                            .map((n) => cell(n, selected, onSelect))}
                    </div>
                </div>
            ))}
        </>
    );
}

function cell(n: Node, selected: string | null, onSelect: (name: string | null) => void, type?: string) {
    return (
        <button
            key={n.name}
            type="button"
            className={`node-cell tone-${toneOf(n.health)}`}
            aria-pressed={selected === n.name}
            aria-label={`${n.name}: ${n.health}, ${n.status}, upgrade domain ${n.upgradeDomain}${type ? `, ${type}` : ""}`}
            title={n.reason}
            onClick={() => onSelect(selected === n.name ? null : n.name)}
        >
            <span className="node-cell-name">{n.name}</span>
            <span className="node-cell-sub">
                UD {n.upgradeDomain}
                {type ? ` · ${type}` : ""}
                {n.status !== "Up" ? ` · ${n.status}` : ""}
            </span>
        </button>
    );
}
