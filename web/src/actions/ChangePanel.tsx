import { useRef, useState } from "react";
import type { ReactNode } from "react";
import type { Action } from "../lib/types";
import { Button } from "../ui/Button";
import { Icon } from "../ui/Icon";
import { Notice } from "../ui/Notice";
import { SidePanel } from "../ui/Overlay";
import { Section } from "../ui/Section";
import { errorText, subject, useRun } from "./run";
import "./actions.css";

export type Change = { name: string; from?: string; to?: string; note?: string };

/** What an editor has worked out from its form: what changed, whether it's valid, what it will cause, what to send. */
export type Draft = {
    changes: Change[];
    unchanged?: number;
    errors: number;
    effect: string;
    inputs: Record<string, string>;
    submitLabel: string;
};

/**
 * The side panel every change with a form goes through: edit (the editor's fields), then review (the diff, the
 * effect, how to roll it out), then run. Editors supply the fields and the draft; this owns the flow.
 */
export function ChangePanel({
    action,
    draft,
    rollout,
    onClose,
    children,
}: {
    action: Action;
    draft: Draft;
    rollout?: ReactNode;
    onClose: () => void;
    children: ReactNode;
}) {
    const run = useRun();
    const [step, setStep] = useState<"edit" | "review">("edit");
    const [running, setRunning] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const dirty = draft.changes.length > 0;
    // A double click on "Review changes" mustn't land on the button that replaces it and submit unseen.
    const reviewedAt = useRef(0);
    const [, rerender] = useState(0);
    const toReview = () => {
        reviewedAt.current = Date.now();
        setStep("review");
        setTimeout(() => rerender((n) => n + 1), 450);
    };

    const submit = async () => {
        setRunning(true);
        setError(null);
        try {
            await run(action, draft.inputs);
            onClose();
        } catch (err) {
            setError(errorText(err));
            setRunning(false);
        }
    };

    return (
        <SidePanel
            title={`${action.title} ${subject(action.target)}`}
            subtitle={
                <ol className="steps" aria-label="Steps">
                    <li aria-current={step === "edit" ? "step" : undefined}>Edit</li>
                    <Icon name="chevron-right" className="crumb-sep" />
                    <li aria-current={step === "review" ? "step" : undefined}>Review</li>
                </ol>
            }
            onClose={onClose}
            onCancel={() => !running && (!dirty || confirm("Discard your changes?"))}
            footer={(close) =>
                step === "edit" ? (
                    <>
                        <Button key="review" variant="primary" disabled={!dirty || draft.errors > 0} onClick={toReview}>
                            Review changes
                        </Button>
                        <Button key="cancel" onClick={close}>
                            Cancel
                        </Button>
                        <span className="panel-status">
                            {draft.errors > 0 ? "Fix the highlighted fields to continue." : dirty ? `${draft.changes.length} ${draft.changes.length === 1 ? "change" : "changes"}.` : "Nothing changed yet."}
                        </span>
                    </>
                ) : (
                    <>
                        <Button key="submit" variant="primary" disabled={running || draft.errors > 0 || Date.now() - reviewedAt.current < 400} onClick={submit}>
                            {running ? "Working…" : draft.submitLabel}
                        </Button>
                        <Button key="back" disabled={running} onClick={() => setStep("edit")}>
                            Back
                        </Button>
                    </>
                )
            }
        >
            {step === "edit" ? (
                children
            ) : (
                <>
                    <Section label="Changes">
                        <Diff changes={draft.changes} />
                        {!!draft.unchanged && (
                            <p className="panel-note">
                                {draft.unchanged} other {draft.unchanged === 1 ? "value stays" : "values stay"} as {draft.unchanged === 1 ? "it is" : "they are"}.
                            </p>
                        )}
                    </Section>
                    <Section label="Effect">
                        <p className="change-effect">{draft.effect}</p>
                    </Section>
                    {rollout}
                    {error && <Notice tone="danger">{error}</Notice>}
                </>
            )}
        </SidePanel>
    );
}

function Diff({ changes }: { changes: Change[] }) {
    return (
        <div className="diff">
            {changes.map((c) => (
                <div key={c.name} className="diff-row">
                    <span className="diff-name mono">{c.name}</span>
                    <span className="diff-values">
                        {c.note ? (
                            <span className="secondary">{c.note}</span>
                        ) : (
                            <>
                                <span className="secondary">{c.from || "empty"}</span>
                                <Icon name="arrow-right" className="diff-arrow" />
                                <span className="strong">{c.to || "empty"}</span>
                            </>
                        )}
                    </span>
                </div>
            ))}
        </div>
    );
}
