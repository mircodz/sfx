import { Link } from "./Button";
import "./Fields.css";

/** A fact. Without a value it reads "None"; with an href, the value opens that page (a node, a service). */
export type FieldItem = { name: string; value?: string | null; note?: string; href?: string };

/** Lets long code-like names (Counter_TargetReplicaSetSize) wrap after "_" or "." instead of mid-word. */
function breakable(name: string) {
    return name.split(/(?<=[_.])/).map((part, i) => (
        <span key={i}>
            {i > 0 && <wbr />}
            {part}
        </span>
    ));
}

/** Key-value facts, two columns of pairs on wide screens. With `mono`, names are code-like (parameters). */
export function Fields({ fields, mono }: { fields: FieldItem[]; mono?: boolean }) {
    return (
        <dl className="fields">
            {fields.map((f) => (
                <div key={f.name} className="field">
                    <dt className={mono ? "mono" : undefined}>{mono ? breakable(f.name) : f.name}</dt>
                    <dd>
                        {!f.value ? <span className="muted">None</span> : f.href ? <Link href={f.href}>{f.value}</Link> : f.value}
                        {f.note && <span className="field-note"> · {f.note}</span>}
                    </dd>
                </div>
            ))}
        </dl>
    );
}
