import { natural } from "../lib/format";
import { toneOf } from "../lib/health";
import type { Node } from "../lib/types";
import "./NodeMap.css";

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
    // Beyond this many, named cells don't fit at a glance: small squares, still links, with the details in a tooltip.
    if (nodes.length > denseAbove) return <DenseMap nodes={nodes} onSelect={onSelect} />;
    const types = [...new Set(nodes.map((n) => n.nodeType))].sort();
    // Mostly one node per type: headings would stack single cells; one grid with the type on each cell says the same.
    if (!grouped || types.length * 2 > nodes.length) {
        const sorted = [...nodes].sort((a, b) => a.upgradeDomain.localeCompare(b.upgradeDomain, undefined, { numeric: true }) || a.name.localeCompare(b.name));
        return <div className="node-map">{sorted.map((n) => cell(n, selected, onSelect, n.nodeType))}</div>;
    }
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

const denseAbove = 60;

const byDomain = (a: Node, b: Node) => natural(a.upgradeDomain, b.upgradeDomain) || natural(a.name, b.name);

/** For hundreds of nodes: one square each, by node type, in upgrade domain order. */
function DenseMap({ nodes, onSelect }: { nodes: Node[]; onSelect: (name: string | null) => void }) {
    const types = [...new Set(nodes.map((n) => n.nodeType))].sort();
    return (
        <div className="dense-map">
            {types.map((type) => {
                const members = nodes.filter((n) => n.nodeType === type).sort(byDomain);
                const bad = members.filter((n) => n.health === "Warning" || n.health === "Error").length;
                return (
                    <div key={type} className="dense-map-group">
                        <div className="dense-map-label">
                            <span>{type}</span>
                            <span className="secondary">
                                {members.length}
                                {bad ? ` · ${bad} unhealthy` : ""}
                            </span>
                        </div>
                        <div className="dense-map-cells">
                            {members.map((n) => (
                                <button
                                    key={n.name}
                                    type="button"
                                    className={`dense-cell tone-${toneOf(n.health)} ${n.status !== "Up" ? "not-up" : ""}`}
                                    aria-label={`${n.name}: ${n.health}, ${n.status}, upgrade domain ${n.upgradeDomain}`}
                                    title={`${n.name} · UD ${n.upgradeDomain} · ${n.status} · ${n.health}${n.reason ? `\n${n.reason}` : ""}`}
                                    onClick={() => onSelect(n.name)}
                                />
                            ))}
                        </div>
                    </div>
                );
            })}
        </div>
    );
}
