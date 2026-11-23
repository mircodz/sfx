import { useState } from "react";
import { api } from "../../lib/api";
import { duration } from "../../lib/format";
import { useResource } from "../../lib/resource";
import type { Action, ServiceSettingInfo } from "../../lib/types";
import { Field, Select } from "../../ui/Form";
import { Load } from "../../ui/Load";
import { ChangePanel } from "../ChangePanel";
import type { Change } from "../ChangePanel";

/**
 * Every setting a service can change without being recreated (replica counts, waits, placement, move cost, DNS
 * name), in one form and one update. The server checks each value again; this catches what it can as you type.
 */
export function ServiceSettingsEditor({ action, appId, serviceId, onClose }: { action: Action; appId: string; serviceId: string; onClose: () => void }) {
    const info = useResource(`service-settings:${serviceId}`, () => api.serviceSettings(appId, serviceId), false);
    return (
        <Load resource={info} shape="lines">
            {(d) => <SettingsForm action={d.action ?? action} settings={d.settings} onClose={onClose} />}
        </Load>
    );
}

function SettingsForm({ action, settings, onClose }: { action: Action; settings: ServiceSettingInfo[]; onClose: () => void }) {
    const [values, setValues] = useState<Record<string, string>>({});
    const value = (s: ServiceSettingInfo) => values[s.key] ?? s.value;
    const errors = Object.fromEntries(settings.map((s) => [s.key, check(s, value(s))]));
    const changed = settings.filter((s) => value(s) !== s.value);
    const changes: Change[] = changed.map((s) => ({ name: s.title, from: s.value || "Default", to: value(s) || "Default" }));
    const draft = {
        changes,
        unchanged: settings.length - changed.length,
        errors: changed.filter((s) => errors[s.key]).length,
        effect: "Applied in one update, without an upgrade. Replica counts and placement changes move replicas; the rest take effect the next time they apply.",
        inputs: Object.fromEntries(changed.map((s) => [s.key, value(s)])),
        submitLabel: changed.length === 1 ? "Update 1 setting" : `Update ${changed.length} settings`,
    };
    const set = (key: string, v: string) => setValues((all) => ({ ...all, [key]: v }));
    const revert = (key: string) =>
        setValues((all) => {
            const { [key]: _, ...rest } = all;
            return rest;
        });

    return (
        <ChangePanel action={action} draft={draft} onClose={onClose}>
            {settings.map((s) => (
                <Field
                    key={s.key}
                    label={s.title}
                    help={help(s, value(s))}
                    error={value(s) !== s.value ? errors[s.key] : null}
                    changed={value(s) !== s.value}
                    onRevert={() => revert(s.key)}
                >
                    {(id) =>
                        s.kind === "flag" ? (
                            <Select id={id} value={value(s)} onChange={(v) => set(s.key, v)}>
                                <option value="">Default</option>
                                <option value="true">Yes</option>
                                <option value="false">No</option>
                            </Select>
                        ) : s.choices?.length ? (
                            <Select id={id} value={value(s)} onChange={(v) => set(s.key, v)}>
                                {!s.choices.includes(s.value) && <option value={s.value}>{s.value || "Default"}</option>}
                                {s.choices.map((c) => (
                                    <option key={c} value={c}>
                                        {c}
                                    </option>
                                ))}
                            </Select>
                        ) : (
                            <input id={id} value={value(s)} placeholder="Default" spellCheck={false} onChange={(e) => set(s.key, e.target.value)} />
                        )
                    }
                </Field>
            ))}
        </ChangePanel>
    );
}

/** What the setting takes, and its bounds. */
function help(s: ServiceSettingInfo, v: string) {
    const bounds = s.min != null && s.max != null ? ` from ${s.min} to ${s.max}` : s.min != null ? `, at least ${s.min}` : s.max != null ? `, at most ${s.max}` : "";
    if (s.kind === "seconds") {
        // Plain seconds are hard to read past a minute: say what they come to.
        const n = /^\d+$/.test(v.trim()) ? Number(v) : null;
        const reads = n === null || n < 60 ? "" : n >= 10 * 365 * 86400 ? ` ${v} is no limit.` : ` ${v} is ${duration(n)}.`;
        return `A duration: 30, 30s, 5m or 1h${bounds}.${reads}`;
    }
    if (s.kind === "count") return `A whole number${bounds}.`;
    if (s.key.includes("placement")) return "A placement constraint, e.g. NodeType == Back. Empty removes it.";
    return undefined;
}

/** A first check while typing; the server has the last word (it parses every value again). */
export function check(s: ServiceSettingInfo, v: string): string | null {
    if (v.trim() === "") return null;
    if (s.kind === "seconds") {
        return /^\d+(\.\d+)?\s*(s|m|h|d)?$/i.test(v.trim()) ? null : "A duration like 30, 30s, 5m or 1h.";
    }
    if (s.kind === "count") {
        if (!/^-?\d+$/.test(v.trim())) return "A whole number.";
        const n = Number(v);
        if (s.min != null && n < s.min) return `At least ${s.min}.`;
        if (s.max != null && n > s.max) return `At most ${s.max}.`;
    }
    return null;
}
