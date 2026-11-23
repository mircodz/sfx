import type { UpgradeDefaults } from "../../lib/types";
import { Checkbox, Field } from "../../ui/Form";
import { Disclosure } from "../../ui/Disclosure";

/** The upgrade policy the server accepts, as "policy.<name>" inputs. Empty means the cluster's default. */
export type Policy = Record<PolicyKey, string>;

type PolicyKey =
    | "healthCheckWait"
    | "healthCheckStable"
    | "healthCheckRetry"
    | "upgradeDomainTimeout"
    | "upgradeTimeout"
    | "replicaSetCheckTimeout"
    | "forceRestart"
    | "considerWarningAsError"
    | "maxPercentUnhealthyDeployedApplications"
    | "maxPercentUnhealthyServices"
    | "maxPercentUnhealthyPartitionsPerService"
    | "maxPercentUnhealthyReplicasPerPartition"
    | "maxPercentUnhealthyNodes"
    | "maxPercentUnhealthyApplications";

/** Application upgrades judge services, partitions and replicas; cluster upgrades judge nodes and applications. */
export type PolicyScope = "application" | "cluster";

type Spec = { key: PolicyKey; label: string; kind: "seconds" | "percent" | "flag"; monitored: boolean; help?: string; scope?: PolicyScope };

const specs: Spec[] = [
    { key: "healthCheckWait", label: "Health check wait", kind: "seconds", monitored: true, help: "After a domain is done, before checking health." },
    { key: "healthCheckStable", label: "Health check stable for", kind: "seconds", monitored: true, help: "How long health must hold before moving on." },
    { key: "healthCheckRetry", label: "Health check retry timeout", kind: "seconds", monitored: true, help: "How long to keep retrying unhealthy checks." },
    { key: "upgradeDomainTimeout", label: "Upgrade domain timeout", kind: "seconds", monitored: true },
    { key: "upgradeTimeout", label: "Upgrade timeout", kind: "seconds", monitored: true },
    { key: "replicaSetCheckTimeout", label: "Replica set check timeout", kind: "seconds", monitored: false, help: "How long to wait for replicas to be safe to take down." },
    { key: "maxPercentUnhealthyServices", label: "Max unhealthy services", kind: "percent", monitored: true, scope: "application" },
    { key: "maxPercentUnhealthyPartitionsPerService", label: "Max unhealthy partitions per service", kind: "percent", monitored: true, scope: "application" },
    { key: "maxPercentUnhealthyReplicasPerPartition", label: "Max unhealthy replicas per partition", kind: "percent", monitored: true, scope: "application" },
    { key: "maxPercentUnhealthyDeployedApplications", label: "Max unhealthy deployed applications", kind: "percent", monitored: true, scope: "application" },
    { key: "maxPercentUnhealthyNodes", label: "Max unhealthy nodes", kind: "percent", monitored: true, scope: "cluster" },
    { key: "maxPercentUnhealthyApplications", label: "Max unhealthy applications", kind: "percent", monitored: true, scope: "cluster" },
    { key: "considerWarningAsError", label: "Treat warnings as errors", kind: "flag", monitored: true },
    { key: "forceRestart", label: "Restart processes even if the code didn't change", kind: "flag", monitored: false },
];

/** "2m", "1h 30m", "0s": how a default reads next to a field that takes seconds. */
function seconds(n: number) {
    if (n === 0) return "0s";
    const h = Math.floor(n / 3600);
    const m = Math.floor((n % 3600) / 60);
    const s = n % 60;
    return [h && `${h}h`, m && `${m}m`, s && `${s}s`].filter(Boolean).join(" ");
}

/** What the cluster uses when a field is left empty, as shown in its placeholder. */
function defaultOf(key: PolicyKey, d: UpgradeDefaults): string {
    const hp = d.healthPolicy;
    switch (key) {
        case "healthCheckWait":
            return seconds(d.healthCheckWait);
        case "healthCheckStable":
            return seconds(d.healthCheckStable);
        case "healthCheckRetry":
            return seconds(d.healthCheckRetry);
        case "upgradeDomainTimeout":
            return d.upgradeDomainTimeout === null ? "No limit" : seconds(d.upgradeDomainTimeout);
        case "upgradeTimeout":
            return d.upgradeTimeout === null ? "No limit" : seconds(d.upgradeTimeout);
        case "replicaSetCheckTimeout":
            return d.replicaSetCheckTimeout === null ? "No limit" : seconds(d.replicaSetCheckTimeout);
        case "maxPercentUnhealthyServices":
            return `${hp.maxPercentUnhealthyServices}%`;
        case "maxPercentUnhealthyPartitionsPerService":
            return `${hp.maxPercentUnhealthyPartitionsPerService}%`;
        case "maxPercentUnhealthyReplicasPerPartition":
            return `${hp.maxPercentUnhealthyReplicasPerPartition}%`;
        case "maxPercentUnhealthyDeployedApplications":
            return `${hp.maxPercentUnhealthyDeployedApplications}%`;
        case "maxPercentUnhealthyNodes":
        case "maxPercentUnhealthyApplications":
            return "0%";
        case "considerWarningAsError":
            return hp.considerWarningAsError ? "Yes" : "No";
        default:
            return "No";
    }
}

export const emptyPolicy = Object.fromEntries(specs.map((s) => [s.key, ""])) as Policy;

/** Field problems, by key; only fields that apply to the mode are checked, since the others aren't sent. */
const inScope = (s: Spec, scope: PolicyScope) => !s.scope || s.scope === scope;

export function policyErrors(policy: Policy, monitored: boolean, scope: PolicyScope = "application"): Partial<Record<PolicyKey, string>> {
    const errors: Partial<Record<PolicyKey, string>> = {};
    for (const s of specs.filter((x) => inScope(x, scope))) {
        const v = policy[s.key];
        if (v === "" || s.kind === "flag" || (s.monitored && !monitored)) continue;
        if (!/^\d+$/.test(v)) errors[s.key] = s.kind === "seconds" ? "A whole number of seconds." : "A whole percentage.";
        else if (s.kind === "percent" && Number(v) > 100) errors[s.key] = "From 0 to 100.";
    }
    return errors;
}

/** The policy as inputs and as diff lines: only what's set, and only what applies to the mode. */
export function policyInputs(policy: Policy, monitored: boolean, scope: PolicyScope = "application") {
    const inputs: Record<string, string> = {};
    const changes: { name: string; note: string }[] = [];
    for (const s of specs.filter((x) => inScope(x, scope))) {
        const v = policy[s.key];
        if (v === "" || (s.monitored && !monitored)) continue;
        inputs[`policy.${s.key}`] = v;
        changes.push({
            name: s.label,
            note: s.kind === "flag" ? (v === "true" ? "Yes" : "No") : s.kind === "seconds" ? `${v}s instead of the cluster's default` : `${v}% instead of the cluster's default`,
        });
    }
    return { inputs, changes };
}

/** Timeouts and the health policy, collapsed: most upgrades are fine with the cluster's defaults. */
export function PolicyFields({
    policy,
    onChange,
    monitored,
    defaults,
    scope = "application",
}: {
    policy: Policy;
    onChange: (policy: Policy) => void;
    monitored: boolean;
    defaults: UpgradeDefaults;
    scope?: PolicyScope;
}) {
    const errors = policyErrors(policy, monitored, scope);
    const shown = specs.filter((x) => inScope(x, scope));
    const set = (key: PolicyKey, value: string) => onChange({ ...policy, [key]: value });
    const custom = shown.filter((s) => policy[s.key] !== "" && (!s.monitored || monitored)).length;
    return (
        <Disclosure label="Timeouts and health policy" aside={custom ? `${custom} set` : "Cluster defaults"} initiallyOpen={custom > 0}>
            <p className="panel-note">
                Empty fields use the default shown in them:{" "}
                {scope === "cluster" ? "the cluster's health policy" : defaults.healthPolicy.fromManifest ? "the health policy from the application manifest" : "Service Fabric's health policy"}, and Service
                Fabric's timings. Times are in seconds.
                {!monitored && " Health checks and timeouts only apply to monitored upgrades."}
            </p>
            <div className="field-grid">
                {shown
                    .filter((s) => s.kind !== "flag")
                    .map((s) => (
                        <Field key={s.key} label={s.label} help={s.help} error={errors[s.key]} changed={policy[s.key] !== ""} onRevert={() => set(s.key, "")}>
                            {(id) => (
                                <input
                                    id={id}
                                    inputMode="numeric"
                                    value={s.monitored && !monitored ? "" : policy[s.key]}
                                    placeholder={`${defaultOf(s.key, defaults)} (default)`}
                                    disabled={s.monitored && !monitored}
                                    onChange={(e) => set(s.key, e.target.value.trim())}
                                />
                            )}
                        </Field>
                    ))}
            </div>
            {shown
                .filter((s) => s.kind === "flag")
                .map((s) => (
                    <Checkbox
                        key={s.key}
                        label={
                            <>
                                {s.label} <span className="secondary">(default: {defaultOf(s.key, defaults).toLowerCase()})</span>
                            </>
                        }
                        checked={policy[s.key] === "true"}
                        disabled={s.monitored && !monitored}
                        onChange={(c) => set(s.key, c ? "true" : "")}
                    />
                ))}
        </Disclosure>
    );
}
