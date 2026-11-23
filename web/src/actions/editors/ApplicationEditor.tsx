import { useState } from "react";
import type { Action, ApplicationDetail } from "../../lib/types";
import { isMonitored, modeEffects } from "../../lib/upgrades";
import { ChoiceField, Field, Select } from "../../ui/Form";
import { Empty, Section } from "../../ui/Section";
import { Combobox } from "../../ui/Combobox";
import { SearchBox } from "../../ui/Toolbar";
import { ChangePanel } from "../ChangePanel";
import type { Change } from "../ChangePanel";
import { PolicyFields, emptyPolicy, policyErrors, policyInputs } from "./PolicyFields";
import type { Policy } from "./PolicyFields";

const scaleSettings = ["InstanceCount", "TargetReplicaSetSize", "MinReplicaSetSize"];

/**
 * Changes an application's parameters or version, as a rolling upgrade. Parameters that feed default services are
 * locked when the cluster wouldn't accept the change, saying why, rather than letting the upgrade be refused.
 */
export function ApplicationEditor({ action, app, onClose }: { action: Action; app: ApplicationDetail; onClose: () => void }) {
    const current = app.application.typeVersion;
    const modeInput = action.inputs.find((i) => i.name === "mode");
    const [version, setVersion] = useState(current);
    const [mode, setMode] = useState(modeInput?.default ?? "monitored-rollback");
    const [edits, setEdits] = useState<Record<string, string>>({});
    const [policy, setPolicy] = useState<Policy>(emptyPolicy);
    const [search, setSearch] = useState("");
    // Parameters set here that weren't set on the application, and ones set on it that go back to their default.
    const [added, setAdded] = useState<Set<string>>(new Set());
    const [resets, setResets] = useState<Set<string>>(new Set());

    const values = Object.fromEntries(app.parameters.map((p) => [p.name, p.value]));
    const overridden = new Set(app.parameters.filter((p) => p.overridden).map((p) => p.name));
    const targetDefaults = app.versions.find((v) => v.version === version)?.defaults ?? {};
    const declared = Object.keys(targetDefaults);
    const dropped = Object.keys(values).filter((n) => !(n in targetDefaults));

    // What's set on the application after this change: what's set today (unless reset), plus what's added here.
    const isSet = (n: string) => (overridden.has(n) && !resets.has(n)) || added.has(n);
    const setNames = declared.filter(isSet).sort((a, b) => a.localeCompare(b));
    // Anything not set follows the target version's default, which is what the cluster does.
    const valueOf = (n: string) => edits[n] ?? (isSet(n) && overridden.has(n) ? values[n] : targetDefaults[n]);

    const locked = (name: string): string | null => {
        const use = app.defaultServices.uses.find((u) => u.parameter === name && (u.partitioning || !app.defaultServices.upgradeEnabled));
        if (!use) return null;
        if (use.partitioning) return `Sets how ${use.service} is partitioned (${use.setting}), which can't change once the service exists.`;
        return scaleSettings.includes(use.setting)
            ? `Sets ${use.service}'s ${use.setting}. Scale ${use.service} from its page instead.`
            : `Sets ${use.service}'s ${use.setting}, which upgrades can't change on this cluster.`;
    };

    const changes: Change[] = [];
    if (version !== current) changes.push({ name: "Version", from: current, to: version });
    for (const name of declared.sort((a, b) => a.localeCompare(b))) {
        const to = valueOf(name);
        if (resets.has(name)) changes.push({ name, note: `Back to the default: ${values[name] || "empty"} to ${to || "empty"}` });
        else if (added.has(name)) changes.push({ name, note: `Set to ${to || "empty"} (the default is ${targetDefaults[name] || "empty"})` });
        else if (!(name in values)) changes.push({ name, note: `New in ${version}, default ${to || "empty"}` });
        else if (to !== values[name])
            changes.push(edits[name] === undefined ? { name, note: `Follows the new default: ${values[name] || "empty"} to ${to || "empty"}` } : { name, from: values[name], to });
    }
    for (const name of dropped) changes.push({ name, note: `Dropped by ${version}` });

    const q = search.toLowerCase();
    const listed = setNames.filter((n) => !q || `${n} ${valueOf(n)}`.toLowerCase().includes(q));
    const addable = declared
        .filter((n) => !isSet(n))
        .sort((a, b) => a.localeCompare(b))
        .map((n) => {
            const reason = n in values ? locked(n) : null;
            return {
                value: n,
                search: n,
                label: <span className="mono">{n}</span>,
                hint: reason ?? `default ${targetDefaults[n] || "empty"}`,
                disabled: !!reason,
            };
        });

    const without = <T,>(set: Set<T>, x: T) => {
        const next = new Set(set);
        next.delete(x);
        return next;
    };
    const withoutEdit = (n: string) =>
        setEdits((e) => {
            const next = { ...e };
            delete next[n];
            return next;
        });

    const monitored = isMonitored(mode);
    const policyErrorCount = Object.keys(policyErrors(policy, monitored)).length;
    const custom = policyInputs(policy, monitored);

    const inputs: Record<string, string> = { version, mode, ...custom.inputs };
    for (const name of setNames) if (edits[name] !== undefined || added.has(name)) inputs[`param:${name}`] = valueOf(name);
    for (const name of resets) if (name in targetDefaults) inputs[`reset:${name}`] = "true";

    const draft = {
        changes,
        unchanged: declared.filter((n) => n in values && !resets.has(n) && valueOf(n) === values[n]).length,
        errors: policyErrorCount,
        effect:
            version === current
                ? "A rolling upgrade to the same version with new parameters: services restart domain by domain to pick them up."
                : `A rolling upgrade to ${version}: services restart domain by domain on the new code and configuration.`,
        inputs,
        submitLabel: `Upgrade ${app.application.name}`,
    };

    const rollout = (
        <Section label="Roll out">
            {modeInput?.choices && (
                <ChoiceField
                    label="Upgrade mode"
                    choices={modeInput.choices.map((c) => ({ ...c, hint: modeEffects[c.value] }))}
                    value={mode}
                    onChange={setMode}
                />
            )}
            <PolicyFields policy={policy} onChange={setPolicy} monitored={monitored} defaults={app.upgradeDefaults} />
            {custom.changes.length > 0 && (
                <p className="panel-note">
                    Custom: {custom.changes.map((c) => `${c.name.toLowerCase()} ${c.note}`).join("; ")}.
                </p>
            )}
        </Section>
    );

    return (
        <ChangePanel action={action} draft={draft} rollout={rollout} onClose={onClose}>
            <Field label="Version" help={version === current ? "Keep the version to change only parameters." : undefined} changed={version !== current} onRevert={() => setVersion(current)}>
                {(id) => (
                    <Select id={id} value={version} onChange={setVersion}>
                        {app.versions.map((v) => (
                            <option key={v.version} value={v.version}>
                                {v.version}
                                {v.version === current ? " (current)" : ""}
                            </option>
                        ))}
                    </Select>
                )}
            </Field>
            <Section label="Parameters" aside={`${setNames.length} set · ${declared.length} declared`}>
                {setNames.length > 12 && <SearchBox value={search} onChange={setSearch} placeholder={`Find among ${setNames.length} set parameters`} />}
                {setNames.length === 0 && <Empty>Nothing is set on the application: every parameter has its default. Add one below.</Empty>}
                {setNames.length > 0 && listed.length === 0 && <Empty>No set parameters match.</Empty>}
                {listed.map((name) => {
                    const value = valueOf(name);
                    const reason = name in values ? locked(name) : null;
                    if (reason) {
                        return (
                            <Field key={name} label={<span className="mono">{name}</span>} help={reason}>
                                {(id) => <input id={id} value={value} disabled />}
                            </Field>
                        );
                    }
                    const edited = edits[name] !== undefined && edits[name] !== (overridden.has(name) ? values[name] : targetDefaults[name]);
                    return (
                        <Field
                            key={name}
                            label={<span className="mono">{name}</span>}
                            changed={edited || added.has(name)}
                            onRevert={() => {
                                withoutEdit(name);
                                setAdded((a) => without(a, name));
                            }}
                            help={
                                <span className="param-help">
                                    <span>
                                        {added.has(name) ? "Added here. " : edited ? `Was ${values[name] || "empty"}. ` : ""}
                                        Default: {targetDefaults[name] || "empty"}.
                                    </span>
                                    {overridden.has(name) && (
                                        <button
                                            type="button"
                                            className="text-button"
                                            onClick={() => {
                                                withoutEdit(name);
                                                setResets((r) => new Set(r).add(name));
                                            }}
                                        >
                                            Reset to default
                                        </button>
                                    )}
                                </span>
                            }
                        >
                            {(id) => <input id={id} value={value} spellCheck={false} onChange={(e) => setEdits((s) => ({ ...s, [name]: e.target.value }))} />}
                        </Field>
                    );
                })}

                {[...resets].filter((n) => n in targetDefaults).map((name) => (
                    <Field key={name} label={<span className="mono">{name}</span>} changed onRevert={() => setResets((r) => without(r, name))} help={`Back to its default, ${targetDefaults[name] || "empty"}. Was ${values[name] || "empty"}.`}>
                        {(id) => <input id={id} value={targetDefaults[name]} disabled />}
                    </Field>
                ))}

                {addable.length > 0 && (
                    <Combobox
                        label="Add a parameter"
                        placeholder={`Type to find one of ${addable.length} on their defaults`}
                        options={addable}
                        onPick={(n) => {
                            setAdded((a) => new Set(a).add(n));
                            setEdits((e) => ({ ...e, [n]: targetDefaults[n] }));
                        }}
                    />
                )}
                <p className="panel-note">
                    Only parameters the application type declares can be set; a new one needs a new version of the type.
                    {dropped.length > 0 && ` ${version} drops ${dropped.join(", ")}.`}
                </p>
            </Section>
        </ChangePanel>
    );
}
