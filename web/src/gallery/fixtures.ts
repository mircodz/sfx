import type { Action, ApplicationDetail, Evaluation, HealthReport, Metric, Node, Text, Upgrade } from "../lib/types";

// Made-up data covering every state a component can show, for the gallery. Never sent anywhere.

const ago = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

export const nodes: Node[] = [
    { name: "_Node_0", address: "10.0.0.4", nodeType: "frontend", status: "Up", health: "Healthy", upgradeDomain: "0", faultDomain: "fd:/0", isSeed: true, codeVersion: "10.1.2493.9590", uptimeSeconds: 1_036_800, actions: [] },
    { name: "_Node_1", address: "10.0.0.5", nodeType: "frontend", status: "Up", health: "Warning", upgradeDomain: "1", faultDomain: "fd:/1", isSeed: true, codeVersion: "10.1.2493.9590", uptimeSeconds: 7_200, actions: [] },
    { name: "_Node_2", address: "10.0.0.6", nodeType: "frontend", status: "Down", health: "Error", upgradeDomain: "2", faultDomain: "fd:/2", isSeed: false, codeVersion: "10.1.2493.9590", actions: [] },
    { name: "_Node_3", address: "10.0.1.4", nodeType: "backend", status: "Disabled", health: "Warning", upgradeDomain: "3", faultDomain: "fd:/3", isSeed: false, codeVersion: "10.1.2493.9590", uptimeSeconds: 600, actions: [] },
    { name: "_Node_with_a_rather_long_name_4", address: "10.0.1.5", nodeType: "backend", status: "Up", health: "Unknown", upgradeDomain: "4", faultDomain: "fd:/4", isSeed: false, codeVersion: "10.1.2493.9590", uptimeSeconds: 90_000, actions: [] },
];

export const evaluations: Evaluation[] = [
    { depth: 0, kind: "Applications", health: "Error", description: "50% (1/2) applications are unhealthy. The evaluation tolerates 0% unhealthy applications." },
    { depth: 1, kind: "Application", health: "Error", description: "Application 'fabric:/search' is in Error." },
    { depth: 2, kind: "Services", health: "Error", description: "100% (1/1) services of service type 'CounterType' are unhealthy." },
    { depth: 3, kind: "Event", health: "Error", description: "'sfx-demo' reported Error for property 'Latency'." },
    { depth: 1, kind: "Application", health: "Warning", description: "Application 'fabric:/orders' is in Warning." },
];

export const health: HealthReport = {
    health: "Error",
    evaluations,
    events: [
        { source: "System.FM", property: "State", health: "Healthy", description: "Partition is healthy.", modified: ago(300), expired: false },
        { source: "sfx-demo", property: "Latency", health: "Error", description: "p99 latency 2.4s, above the 1s objective.\nSecond line of detail that keeps going so the clamp has something to do.", modified: ago(4), expired: false },
        { source: "Watchdog", property: "Disk", health: "Warning", description: "Disk 87% full.", modified: ago(120), expired: true },
    ],
};

export const metrics: Metric[] = [
    { name: "servicefabric:/_CpuCores", value: 1.5, capacity: 4 },
    { name: "servicefabric:/_MemoryInMB", value: 14_500, capacity: 16_000 },
    { name: "Ticks", value: 239 },
    { name: "Requests", value: 0 },
];

export const upgradeRunning: Upgrade = {
    name: "fabric:/orders",
    kind: "application",
    applicationId: "orders",
    from: "1.0.0",
    target: "1.1.0",
    state: "RollingForwardInProgress",
    mode: "Monitored",
    started: ago(26),
    currentDomain: "2",
    nextDomain: "3",
    domains: ["0", "1", "2", "3", "4"].map((name, i) => ({ name, state: i < 2 ? "Completed" : i === 2 ? "InProgress" : "Pending" })),
    nodes: [{ node: "_Node_2", phase: "PreUpgradeSafetyCheck", pendingSafetyChecks: ["EnsurePartitionQuorum"] }],
    failureReason: "None",
    healthCheckPhase: "WaitDuration",
    evaluations: evaluations.slice(4),
    actions: [],
};

export const upgradeFailed: Upgrade = {
    ...upgradeRunning,
    name: "fabric:/search",
    state: "Failed",
    healthCheckPhase: "Retry",
    failureReason: "HealthCheck",
    nodes: [],
    evaluations: evaluations.slice(1, 4),
};

export const upgradeDone: Upgrade = {
    ...upgradeRunning,
    name: "Cluster",
    kind: "cluster",
    from: "",
    target: "code 10.1.2493, config 2",
    state: "RollingForwardCompleted",
    currentDomain: "",
    healthCheckPhase: "",
    domains: upgradeRunning.domains.map((d) => ({ ...d, state: "Completed" })),
    nodes: [],
    evaluations: [],
};

const sign = "gallery";

export const actions: Record<string, Action> = {
    restart: { id: "node.restart", title: "Restart", risk: "destructive", warning: "Every replica on it restarts or fails over to another node.", confirmText: "_Node_1", target: { node: "_Node_1", status: "Up" }, inputs: [], signature: sign, form: false },
    deactivate: {
        id: "node.deactivate",
        title: "Deactivate",
        risk: "confirm",
        warning: "Pause stops placing replicas on it; restart also moves primaries off and closes replicas.",
        target: { node: "_Node_1", status: "Up" },
        inputs: [{ name: "intent", label: "Why", kind: "choice", default: "Pause", choices: [{ value: "Pause", label: "Pause", hint: "Replicas stay." }, { value: "Restart", label: "Restart", hint: "For a restart." }] }],
        signature: sign,
        form: false,
    },
    move: {
        id: "replica.move",
        title: "Move",
        risk: "confirm",
        target: { partitionId: "7be2a1c4-0000-0000-0000-000000000000", replicaId: "134395751766606130", node: "_Node_1", role: "Primary", serviceId: "orders~Counter", serviceKind: "Stateful" },
        inputs: [{ name: "node", label: "Move to", kind: "node", default: "" }, { name: "ignoreConstraints", label: "Ignore placement constraints", kind: "flag" }],
        signature: sign,
        form: false,
    },
    scale: { id: "service.scale", title: "Scale", risk: "confirm", warning: "The cluster adds or removes replicas to match; nothing restarts.", target: { serviceId: "orders~Counter", applicationId: "orders", name: "fabric:/orders/Counter", kind: "Stateful", count: "3", min: "2" }, inputs: [], signature: sign, form: true },
    rollback: { id: "upgrade.rollback", title: "Roll back", risk: "confirm", warning: "Domains already upgraded go back to the previous version.", target: { kind: "application", applicationId: "orders", name: "fabric:/orders", state: "RollingForwardInProgress", nextDomain: "3" }, inputs: [], signature: sign, form: false },
    resume: { id: "upgrade.resume", title: "Resume into next domain", risk: "confirm", target: { kind: "application", applicationId: "orders", name: "fabric:/orders", state: "RollingForwardPending", nextDomain: "3" }, inputs: [], signature: sign, form: false },
    remove: { id: "replica.remove", title: "Remove", risk: "destructive", warning: "The cluster closes it and builds a replacement elsewhere.", confirmText: "134395751766606130", target: {}, inputs: [], signature: sign, form: false },
};

// Enough declared parameters to feel like a real, large application type.
const many = Object.fromEntries(Array.from({ length: 300 }, (_, i) => [`Setting_${String(i).padStart(3, "0")}_${["Timeout", "Endpoint", "Retries", "Feature"][i % 4]}`, String(i % 7)]));

export const application: ApplicationDetail = {
    application: { id: "orders", name: "fabric:/orders", typeName: "DemoAppType", typeVersion: "1.0.0", status: "Ready", health: "Warning" },
    health,
    parameters: [
        { name: "Counter_PartitionCount", value: "5", default: "1", overridden: true },
        { name: "Counter_TargetReplicaSetSize", value: "3", default: "3", overridden: false },
        { name: "LogLevel", value: "Info", default: "Info", overridden: false },
        { name: "Endpoint_Url_With_A_Long_Name", value: "https://orders.internal:8443", default: "", overridden: true },
        ...Object.entries(many).map(([name, value], i) => ({ name, value: i % 50 === 0 ? "42" : value, default: value, overridden: i % 50 === 0 })),
    ],
    versions: [
        { version: "1.1.0", defaults: { ...many, Counter_PartitionCount: "1", Counter_TargetReplicaSetSize: "3", LogLevel: "Warning", Endpoint_Url_With_A_Long_Name: "", Tracing: "off" } },
        { version: "1.0.0", defaults: { ...many, Counter_PartitionCount: "1", Counter_TargetReplicaSetSize: "3", LogLevel: "Info", Endpoint_Url_With_A_Long_Name: "" } },
    ],
    services: [],
    actions: [],
    upgradeDefaults: {
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
    },
    defaultServices: {
        upgradeEnabled: false,
        uses: [
            { parameter: "Counter_PartitionCount", service: "Counter", setting: "PartitionCount", partitioning: true },
            { parameter: "Counter_TargetReplicaSetSize", service: "Counter", setting: "TargetReplicaSetSize", partitioning: false },
        ],
    },
};

export const upgradeAction: Action = {
    id: "application.upgrade",
    title: "Edit",
    risk: "confirm",
    target: { applicationId: "orders", name: "fabric:/orders", typeName: "DemoAppType", version: "1.0.0" },
    inputs: [
        { name: "version", label: "Version", kind: "version", default: "1.0.0" },
        {
            name: "mode",
            label: "Upgrade mode",
            kind: "choice",
            default: "monitored-rollback",
            choices: [
                { value: "monitored-rollback", label: "Monitored, roll back on failure" },
                { value: "monitored-manual", label: "Monitored, stop on failure" },
                { value: "unmonitored-auto", label: "Unmonitored, automatic" },
                { value: "unmonitored-manual", label: "Unmonitored, manual" },
            ],
        },
        { name: "parameters", label: "Parameters", kind: "parameters" },
    ],
    signature: sign,
    form: true,
};

export const xml: Text = {
    format: "xml",
    text: `<?xml version="1.0" encoding="utf-8"?>
<!-- A comment -->
<ApplicationManifest ApplicationTypeName="DemoAppType" ApplicationTypeVersion="1.0.0" xmlns="http://schemas.microsoft.com/2011/01/fabric">
  <Parameters>
    <Parameter Name="Counter_PartitionCount" DefaultValue="1" />
  </Parameters>
  <DefaultServices>
    <Service Name="Counter"><StatefulService ServiceTypeName="CounterType" TargetReplicaSetSize="[Counter_TargetReplicaSetSize]" /></Service>
  </DefaultServices>
</ApplicationManifest>`,
};

export const json: Text = {
    format: "json",
    text: JSON.stringify({ Id: "orders", Name: "fabric:/orders", Healthy: true, Replicas: 3, Ratio: 0.5, Owner: null, Tags: ["a", "<b>"] }, null, 2),
};
