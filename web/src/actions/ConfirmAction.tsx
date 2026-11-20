import { useState } from "react";
import type { FormEvent } from "react";
import { lowerFirst } from "../lib/format";
import type { Action } from "../lib/types";
import { Button } from "../ui/Button";
import { Field } from "../ui/Form";
import { Dialog } from "../ui/Overlay";
import { Notice } from "../ui/Notice";
import { ActionInputField, defaultInputs } from "./inputs";
import { errorText, subject, useRun } from "./run";

/** Confirms an action without a form: what happens, what it causes, its few inputs, and the typed name if destructive. */
export function ConfirmAction({ action, onClose }: { action: Action; onClose: () => void }) {
    const run = useRun();
    const [inputs, setInputs] = useState(() => defaultInputs(action.inputs));
    const [confirm, setConfirm] = useState("");
    const [running, setRunning] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const destructive = action.risk === "destructive";
    const confirmed = !destructive || !action.confirmText || confirm === action.confirmText;

    const submit = async (e: FormEvent) => {
        e.preventDefault();
        if (!confirmed || running) return;
        setRunning(true);
        setError(null);
        try {
            await run(action, inputs, confirm);
            onClose();
        } catch (err) {
            setError(errorText(err));
            setRunning(false);
        }
    };

    return (
        <Dialog
            title={`${action.title} ${lowerFirst(subject(action.target))}`}
            onClose={onClose}
            footer={
                <>
                    <Button type="submit" form="confirm-form" variant={destructive ? "danger-filled" : "primary"} disabled={!confirmed || running}>
                        {running ? "Working…" : action.title}
                    </Button>
                    <Button onClick={onClose}>Cancel</Button>
                </>
            }
        >
            <form id="confirm-form" className="confirm-form" onSubmit={submit}>
                {action.warning && <p className="confirm-effect">{action.warning}</p>}
                {action.inputs.map((input) => (
                    <ActionInputField
                        key={input.name}
                        input={input}
                        target={action.target}
                        value={inputs[input.name] ?? ""}
                        onChange={(v) => setInputs((s) => ({ ...s, [input.name]: v }))}
                    />
                ))}
                {destructive && action.confirmText && (
                    <Field
                        label={
                            <>
                                Type <span className="mono">{action.confirmText}</span> to confirm.
                            </>
                        }
                    >
                        {(id) => <input id={id} autoFocus value={confirm} onChange={(e) => setConfirm(e.target.value)} spellCheck={false} autoComplete="off" />}
                    </Field>
                )}
                {error && <Notice tone="danger">{error}</Notice>}
            </form>
        </Dialog>
    );
}
