import { useState } from "react";
import { api } from "../../lib/api";
import { useResource } from "../../lib/resource";
import type { Action, UpgradeDefaults } from "../../lib/types";
import { isMonitored, modeEffects } from "../../lib/upgrades";
import { ChoiceField, Field, Select } from "../../ui/Form";
import { Notice } from "../../ui/Notice";
import { Section } from "../../ui/Section";
import { ChangePanel } from "../ChangePanel";
import type { Change } from "../ChangePanel";
import { PolicyFields, emptyPolicy, policyErrors, policyInputs } from "./PolicyFields";
import type { Policy } from "./PolicyFields";

/** Service Fabric's own defaults for a cluster upgrade's timings and health policy. */
const clusterDefaults: UpgradeDefaults = {
    healthCheckWait: 0,
    healthCheckStable: 120,
    healthCheckRetry: 600,
    upgradeDomainTimeout: null,
    upgradeTimeout: null,
    replicaSetCheckTimeout: null,
    healthPolicy: {
        considerWarningAsError: false,
        maxPercentUnhealthyDeployedApplications: 0,
        maxPercentUnhealthyServices: 0,
        maxPercentUnhealthyPartitionsPerService: 0,
        maxPercentUnhealthyReplicasPerPartition: 0,
        fromManifest: false,
    },
};

/** Not offered on Azure-managed clusters. */
export function ClusterUpgradeEditor({ action, onClose }: { action: Action; onClose: () => void }) {
    const options = useResource("cluster-upgrade-options", api.clusterUpgradeOptions, false);
    const o = options.data;
    const modeInput = action.inputs.find((i) => i.name === "mode");
    const [code, setCode] = useState<string | null>(null);
    const [config, setConfig] = useState<string | null>(null);
    const [mode, setMode] = useState(modeInput?.default ?? "monitored-rollback");
    const [policy, setPolicy] = useState<Policy>(emptyPolicy);

    const codeTo = code ?? o?.codeVersion ?? "";
    const configTo = config ?? o?.configVersion ?? "";
    const monitored = isMonitored(mode);
    const custom = policyInputs(policy, monitored, "cluster");

    const changes: Change[] = [];
    if (o && codeTo !== o.codeVersion) changes.push({ name: "Code version", from: o.codeVersion, to: codeTo });
    if (o && configTo !== o.configVersion) changes.push({ name: "Config version", from: o.configVersion, to: configTo });

    const inputs: Record<string, string> = { mode, ...custom.inputs };
    if (o && codeTo !== o.codeVersion) inputs.codeVersion = codeTo;
    if (o && configTo !== o.configVersion) inputs.configVersion = configTo;

    const draft = {
        changes,
        errors: Object.keys(policyErrors(policy, monitored, "cluster")).length + (o?.azureManaged ? 1 : 0),
        effect: "A rolling upgrade of the cluster: nodes restart domain by domain on the new code or configuration. Replicas move off each node before it restarts.",
        inputs,
        submitLabel: "Upgrade the cluster",
    };

    const versionField = (label: string, current: string | undefined, versions: string[] | undefined, value: string, set: (v: string) => void) => (
        <Field label={label} changed={!!current && value !== current} onRevert={() => set(current ?? "")} help={versions && versions.length <= 1 ? "Only the current version is provisioned." : undefined}>
            {(id) => (
                <Select id={id} value={value} onChange={set} disabled={!versions}>
                    {(versions ?? [value]).map((v) => (
                        <option key={v} value={v}>
                            {v}
                            {v === current ? " (current)" : ""}
                        </option>
                    ))}
                </Select>
            )}
        </Field>
    );

    return (
        <ChangePanel
            action={action}
            draft={draft}
            onClose={onClose}
            rollout={
                <Section label="Roll out">
                    {modeInput?.choices && <ChoiceField label="Upgrade mode" choices={modeInput.choices.map((c) => ({ ...c, hint: modeEffects[c.value] }))} value={mode} onChange={setMode} />}
                    <PolicyFields policy={policy} onChange={setPolicy} monitored={monitored} defaults={clusterDefaults} scope="cluster" />
                </Section>
            }
        >
            {options.error && <Notice tone="danger">{options.error}</Notice>}
            {o?.azureManaged && <Notice tone="warning">{o.note}</Notice>}
            {versionField("Code version", o?.codeVersion, o?.codeVersions, codeTo, setCode)}
            {versionField("Config version", o?.configVersion, o?.configVersions, configTo, setConfig)}
            <p className="panel-note">Versions are the ones provisioned in the cluster's image store; provision a new one first to upgrade to it.</p>
        </ChangePanel>
    );
}
