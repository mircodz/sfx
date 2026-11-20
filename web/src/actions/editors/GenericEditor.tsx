import { useState } from "react";
import type { Action } from "../../lib/types";
import { ChangePanel } from "../ChangePanel";
import { ActionInputField, defaultInputs } from "../inputs";

/** Any other action with a form: its inputs as the server described them. */
export function GenericEditor({ action, onClose }: { action: Action; onClose: () => void }) {
    const initial = defaultInputs(action.inputs);
    const [inputs, setInputs] = useState(initial);
    const draft = {
        changes: action.inputs
            .filter((i) => (inputs[i.name] ?? "") !== (initial[i.name] ?? ""))
            .map((i) => ({ name: i.label, from: initial[i.name], to: inputs[i.name] })),
        errors: 0,
        effect: action.warning ?? "",
        inputs,
        submitLabel: action.title,
    };
    return (
        <ChangePanel action={action} draft={draft} onClose={onClose}>
            {action.inputs.map((input) => (
                <ActionInputField
                    key={input.name}
                    input={input}
                    target={action.target}
                    value={inputs[input.name] ?? ""}
                    onChange={(v) => setInputs((s) => ({ ...s, [input.name]: v }))}
                />
            ))}
        </ChangePanel>
    );
}
