import { useId } from "react";
import type { ReactNode } from "react";
import { IconButton } from "./Button";
import { Icon } from "./Icon";
import "./Form.css";

/**
 * A labelled control with help or an error underneath. A changed field gets an accent edge and a revert button;
 * `children` gets the control's id so the label points at it.
 */
export function Field({
    label,
    help,
    error,
    changed,
    onRevert,
    children,
}: {
    label: ReactNode;
    help?: ReactNode;
    error?: string | null;
    changed?: boolean;
    onRevert?: () => void;
    children: (id: string) => ReactNode;
}) {
    const id = useId();
    return (
        <div className={`field-control ${changed ? "changed" : ""}`}>
            <label className="field-label" htmlFor={id}>
                {label}
            </label>
            <div className="field-input">
                {children(id)}
                {changed && onRevert && <IconButton icon="arrow-back-up" label="Revert" onClick={onRevert} />}
            </div>
            {error ? <div className="field-help field-error">{error}</div> : help && <div className="field-help">{help}</div>}
        </div>
    );
}

/** A labelled set of mutually exclusive choices, with the picked one's explanation underneath. */
export function ChoiceField<T extends string>({
    label,
    choices,
    value,
    onChange,
}: {
    label: string;
    choices: { value: T; label: string; hint?: string | null }[];
    value: T;
    onChange: (value: T) => void;
}) {
    const picked = choices.find((c) => c.value === value);
    return (
        <fieldset className="field-control">
            <legend className="field-label">{label}</legend>
            <div className="chips">
                {choices.map((c) => (
                    <button key={c.value} type="button" className="chip" aria-pressed={value === c.value} onClick={() => onChange(c.value)}>
                        {c.label}
                    </button>
                ))}
            </div>
            {picked?.hint && <div className="field-help">{picked.hint}</div>}
        </fieldset>
    );
}

export function Checkbox({ label, checked, onChange, disabled }: { label: ReactNode; checked: boolean; onChange: (checked: boolean) => void; disabled?: boolean }) {
    return (
        <label className={`checkbox ${disabled ? "disabled" : ""}`}>
            <input type="checkbox" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
            {label}
        </label>
    );
}

/** A native select styled like the text inputs, with our chevron instead of each OS's own arrow. */
export function Select({ id, value, onChange, disabled, children }: { id?: string; value: string; onChange: (value: string) => void; disabled?: boolean; children: ReactNode }) {
    return (
        <span className="select">
            <select id={id} value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
                {children}
            </select>
            <Icon name="chevron-down" className="select-chevron" />
        </span>
    );
}
