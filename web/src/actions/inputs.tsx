import { useEffect, useState } from "react";
import { api } from "../lib/api";
import type { ActionInput, Node } from "../lib/types";
import { Checkbox, ChoiceField, Field, Select } from "../ui/Form";

/** A simple input as the server describes it; richer forms (versions, parameters) have editors. */
export function ActionInputField({
    input,
    target,
    value,
    onChange,
}: {
    input: ActionInput;
    target: Record<string, string>;
    value: string;
    onChange: (value: string) => void;
}) {
    switch (input.kind) {
        case "text":
            return (
                <Field label={input.name.startsWith("param:") ? <span className="mono">{input.label}</span> : input.label}>
                    {(id) => <input id={id} value={value} spellCheck={false} onChange={(e) => onChange(e.target.value)} />}
                </Field>
            );
        case "number":
            return <Field label={input.label}>{(id) => <input id={id} type="number" value={value} onChange={(e) => onChange(e.target.value)} />}</Field>;
        case "flag":
            return <Checkbox label={input.label} checked={value === "true"} onChange={(c) => onChange(c ? "true" : "false")} />;
        case "choice":
            return <ChoiceField label={input.label} choices={input.choices ?? []} value={value} onChange={onChange} />;
        case "node":
            return <NodePicker label={input.label} exclude={target.node} value={value} onChange={onChange} />;
        default:
            return null;
    }
}

/** Nodes that aren't up can't be picked. */
export function NodePicker({ label, exclude, value, onChange }: { label: string; exclude?: string; value: string; onChange: (value: string) => void }) {
    const [nodes, setNodes] = useState<Node[] | null>(null);
    useEffect(() => {
        api.nodes().then(setNodes, () => setNodes([]));
    }, []);
    const candidates = (nodes ?? []).filter((n) => n.name !== exclude);
    const types = [...new Set(candidates.map((n) => n.nodeType))].sort();
    return (
        <Field label={label} help="Nodes that aren't up can't take a replica.">
            {(id) => (
                <Select id={id} value={value} onChange={onChange} disabled={nodes === null}>
                    <option value="">Let the cluster choose</option>
                    {types.map((t) => (
                        <optgroup key={t} label={t}>
                            {candidates
                                .filter((n) => n.nodeType === t)
                                .map((n) => (
                                    <option key={n.name} value={n.name} disabled={n.status !== "Up"}>
                                        {n.name} · UD {n.upgradeDomain}
                                        {n.status !== "Up" ? ` · ${n.status}` : ""}
                                    </option>
                                ))}
                        </optgroup>
                    ))}
                </Select>
            )}
        </Field>
    );
}

export function defaultInputs(inputs: ActionInput[]) {
    return Object.fromEntries(inputs.filter((i) => i.default != null).map((i) => [i.name, i.default!]));
}
