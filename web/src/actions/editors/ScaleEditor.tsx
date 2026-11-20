import { useState } from "react";
import type { Action } from "../../lib/types";
import { Field } from "../../ui/Form";
import { ChangePanel } from "../ChangePanel";
import type { Change } from "../ChangePanel";

const whole = /^-?\d+$/;

/** Scales a service: instance count, or target and minimum replica set size. Applied right away, no upgrade. */
export function ScaleEditor({ action, onClose }: { action: Action; onClose: () => void }) {
    const stateful = action.target.kind === "Stateful";
    const count0 = action.target.count ?? "";
    const min0 = action.target.min ?? "";
    const [count, setCount] = useState(count0);
    const [min, setMin] = useState(min0);

    const n = Number(count);
    const countError = !whole.test(count)
        ? "A whole number."
        : stateful
          ? n < 1
              ? "At least 1."
              : null
          : n === 0 || n < -1
            ? "At least 1, or -1 for every node."
            : null;
    const minError = !stateful ? null : !/^\d+$/.test(min) ? "A whole number." : Number(min) < 1 ? "At least 1." : Number(min) > n ? "No more than the target size." : null;

    const changes: Change[] = [];
    if (count !== count0) changes.push({ name: stateful ? "Target replica set size" : "Instance count", from: count0, to: count });
    if (stateful && min !== min0) changes.push({ name: "Min replica set size", from: min0, to: min });

    const noun = stateful ? "replicas" : "instances";
    const raisesMin = stateful && min !== min0 && Number(min) > Number(min0);
    const draft = {
        changes,
        errors: (countError ? 1 : 0) + (minError ? 1 : 0),
        effect:
            `Applied right away, without an upgrade or restarts: the cluster ${n > Number(count0) ? "builds new" : "removes"} ${noun} to match.` +
            (raisesMin ? " Partitions with fewer replicas up than the new minimum stop accepting writes until enough come back." : ""),
        inputs: (stateful && min !== min0 ? { count, min } : { count }) as Record<string, string>,
        submitLabel: `Scale ${action.target.name ?? ""}`,
    };

    return (
        <ChangePanel action={action} draft={draft} onClose={onClose}>
            <Field
                label={stateful ? "Target replica set size" : "Instance count"}
                help={stateful ? "How many replicas each partition keeps." : "How many instances run; -1 runs one on every node."}
                error={countError}
                changed={count !== count0}
                onRevert={() => setCount(count0)}
            >
                {(id) => <input id={id} type="number" value={count} onChange={(e) => setCount(e.target.value)} />}
            </Field>
            {stateful && (
                <Field
                    label="Min replica set size"
                    help="Below this many replicas up, a partition stops accepting writes."
                    error={minError}
                    changed={min !== min0}
                    onRevert={() => setMin(min0)}
                >
                    {(id) => <input id={id} type="number" value={min} onChange={(e) => setMin(e.target.value)} />}
                </Field>
            )}
        </ChangePanel>
    );
}
