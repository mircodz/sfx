import { useState } from "react";
import type { Action } from "../../lib/types";
import { ChangePanel } from "../ChangePanel";
import { ActionInputField, defaultInputs } from "../inputs";

/** Any other action with a form: its inputs as the server describes them. */
export function GenericEditor({ action, onClose }: { action: Action; onClose: () => void }) {
    const initial = defaultInputs(action.inputs);
    const [inputs, setInputs] = useState(initial);
    const fields = action.inputs.filter((i) => !i.name.startsWith("param:"));
    const params = action.inputs.filter((i) => i.name.startsWith("param:"));
    const field = (input: (typeof action.inputs)[number]) => (
        <ActionInputField
            key={input.name}
            input={input}
            target={action.target}
            value={inputs[input.name] ?? ""}
            onChange={(v) => setInputs((s) => ({ ...s, [input.name]: v }))}
        />
    );
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
            {fields.map(field)}
            {/* Application parameters (param:<Name>) under their own heading, prefilled with the type's defaults. */}
            {params.length > 0 && (
                <>
                    <h3 className="group-heading">
                        <span>Parameters</span>
                        <span>{params.length}</span>
                    </h3>
                    {params.map(field)}
                </>
            )}
        </ChangePanel>
    );
}
