// Mirrors src/Sfx.Web/Dtos.cs and WebActions.cs.

export type Health = "Healthy" | "Warning" | "Error" | "Unknown";
export type DomainState = "Done" | "Upgrading" | "Verifying" | "Pending" | "Failed" | "Rolled back";
/** "In progress" is an operation or upgrade still running. */
export type Status = Health | DomainState | "In progress" | "Rolling back" | "Cancelled" | "Complete" | "Paused";

export interface Session {
    /** The connection: new on every switch, even back to the same context. */
    id: string;
    context: string;
    endpoint: string;
    readOnly: boolean;
    power: boolean;
    version: string;
    /** The cluster runs the event store; without it, events aren't offered anywhere. */
    eventStore: boolean;
    /** It runs the repair manager; without it, there are no repair tasks. */
    repairManager: boolean;
    /** It runs the Fault Analysis Service; the server offers the actions that need it only then. */
    faultAnalysis: boolean;
    /** The image store can be browsed. */
    imageStore?: boolean;
    folder?: string;
    label?: string;
    color?: FolderColor;
}

export type FolderColor = "success" | "warning" | "danger" | "accent" | "neutral";

export interface ClusterContext {
    name: string;
    endpoint: string;
    auth: string[];
    /** After folder rules: a read-only folder makes every context in it read-only. */
    readOnly: boolean;
    power: boolean;
    /** False for a cluster given on the command line rather than saved in ~/.sfx/config.toml. */
    saved: boolean;
    /** "prod/europe", or absent at the top level. */
    folder?: string;
    /** From the nearest folder that sets one, e.g. "PROD". */
    label?: string;
    color?: FolderColor;
}

export interface Folder {
    path: string;
    label?: string;
    color?: FolderColor;
    readOnly: boolean;
}

export interface Contexts {
    current: string;
    /** The current connection, as in the session's id. */
    session: string;
    contexts: ClusterContext[];
    folders: Folder[];
    /** A switch in progress: sign-in messages so far (e.g. a device code), or why it failed. */
    connecting?: { context: string; messages: string[]; error?: string };
    /** Where contexts are saved, with the home directory as ~. */
    configPath: string;
}

/** What an upgrade uses for anything its policy leaves out. Seconds; null is no limit. */
export interface UpgradeDefaults {
    healthCheckWait: number;
    healthCheckStable: number;
    healthCheckRetry: number;
    upgradeDomainTimeout: number | null;
    upgradeTimeout: number | null;
    replicaSetCheckTimeout: number | null;
    healthPolicy: {
        considerWarningAsError: boolean;
        maxPercentUnhealthyDeployedApplications: number;
        maxPercentUnhealthyServices: number;
        maxPercentUnhealthyPartitionsPerService: number;
        maxPercentUnhealthyReplicasPerPartition: number;
        /** Declared in the application manifest; otherwise Service Fabric's own defaults. */
        fromManifest: boolean;
    };
}

export interface ActionChoice {
    value: string;
    label: string;
    hint?: string | null;
}

export interface ActionInput {
    name: string;
    label: string;
    kind: "number" | "choice" | "flag" | "node" | "version" | "parameters" | "text";
    choices?: ActionChoice[] | null;
    default?: string | null;
}

export interface Action {
    id: string;
    title: string;
    risk: "confirm" | "destructive";
    warning?: string;
    confirmText?: string;
    target: Record<string, string>;
    inputs: ActionInput[];
    signature: string;
    /** Edited in the side panel with a review step, rather than confirmed in a dialog. */
    form: boolean;
}

export interface Version {
    version: string;
    defaults: Record<string, string>;
}

export interface Evaluation {
    depth: number;
    kind: string;
    health: Health;
    description: string;
}

export interface HealthEvent {
    source: string;
    property: string;
    health: Health;
    description: string;
    modified?: string;
    expired: boolean;
}

export interface HealthReport {
    health: Health;
    evaluations: Evaluation[];
    events: HealthEvent[];
}

export interface Cluster {
    name: string;
    endpoint: string;
    codeVersion: string;
    health: HealthReport;
    actions: Action[];
}

export interface Node {
    name: string;
    address: string;
    nodeType: string;
    status: string;
    health: Health;
    upgradeDomain: string;
    faultDomain: string;
    isSeed: boolean;
    codeVersion: string;
    uptimeSeconds?: number;
    actions: Action[];
    /** Why it is unhealthy, for Warning and Error. */
    reason?: string;
}

export interface Metric {
    name: string;
    value: number;
    capacity?: number;
}

export interface DeployedReplica {
    application: string;
    applicationId: string;
    service: string;
    partitionId: string;
    id: string;
    role: string;
    status: string;
    codePackage: string;
}

export interface NodeDetail {
    node: Node;
    health: HealthReport;
    load: Metric[];
    replicas: DeployedReplica[];
}

export interface Application {
    id: string;
    name: string;
    typeName: string;
    typeVersion: string;
    status: string;
    health: Health;
    /** Why it is unhealthy, for Warning and Error. */
    reason?: string;
}

export interface Parameter {
    name: string;
    value: string;
    default?: string;
    /** Set on the application; otherwise it follows its version's default, and so changes with the version. */
    overridden: boolean;
}

export interface Service {
    id: string;
    name: string;
    typeName: string;
    kind: string;
    status: string;
    health: Health;
    /** Why it is unhealthy, for Warning and Error. */
    reason?: string;
}

export interface ApplicationDetail {
    application: Application;
    health: HealthReport;
    parameters: Parameter[];
    versions: Version[];
    upgrade?: Upgrade;
    services: Service[];
    actions: Action[];
    upgradeDefaults: UpgradeDefaults;
    defaultServices: {
        upgradeEnabled: boolean;
        uses: { parameter: string; service: string; setting: string; partitioning: boolean }[];
    };
}

export interface Field {
    name: string;
    value: string;
}

export interface Partition {
    id: string;
    kind: string;
    scheme: string;
    status: string;
    health: Health;
    replicaSet: string;
    /** Int64Range partitions: the key range, as strings since 64-bit keys don't fit a JS number. */
    lowKey?: string | null;
    highKey?: string | null;
    /** Named partitions: the name. */
    name?: string | null;
    /** Why it is unhealthy, for Warning and Error. */
    reason?: string;
}

export interface ServiceDetail {
    service: Service;
    applicationId: string;
    health: HealthReport;
    description: Field[];
    partitions: Partition[];
    actions: Action[];
}

export interface Replica {
    id: string;
    nodeName: string;
    role: string;
    status: string;
    health: Health;
    address: string;
    actions: Action[];
}

export interface PartitionDetail {
    partition: Partition;
    service: Service;
    health: HealthReport;
    load: Metric[];
    replicas: Replica[];
    actions: Action[];
}

export interface Upgrade {
    name: string;
    kind: "cluster" | "application";
    applicationId?: string;
    from: string;
    target: string;
    state: string;
    mode: string;
    started?: string;
    currentDomain: string;
    nextDomain: string;
    domains: { name: string; state: string }[];
    nodes: { node: string; phase: string; pendingSafetyChecks: string[] }[];
    failureReason: string;
    healthCheckPhase: string;
    evaluations: Evaluation[];
    actions: Action[];
}

export type EntityKind = "cluster" | "node" | "application" | "service" | "partition" | "replica";

export interface ClusterEvent {
    time: string;
    kind: string;
    entityKind: EntityKind;
    category: string;
    entity: string;
    details: string;
}

export interface Events {
    events: ClusterEvent[];
    unavailable?: string;
}

export interface Activity {
    time: string;
    user: string;
    context: string;
    action: string;
    outcome: string;
    succeeded: boolean;
}

export interface Text {
    text: string;
    format: "xml" | "json" | "none";
}

/** A replica or instance anywhere in the cluster, with its owners. */
export interface ClusterReplica {
    applicationId: string;
    application: string;
    serviceId: string;
    service: string;
    serviceKind: string;
    partitionId: string;
    partitionStatus: string;
    partitionHealth: Health;
    replicaId: string;
    node: string;
    role: string;
    status: string;
    health: Health;
}

/** A change made through sfx, followed until the cluster is actually done with it. */
export interface Operation {
    id: string;
    actionId: string;
    title: string;
    started: string;
    state: "running" | "done" | "failed";
    detail?: string;
    finished?: string;
    /** 0 to 1 while a package uploads. */
    progress?: number | null;
}

export interface ClusterUpgradeOptions {
    codeVersion: string;
    configVersion: string;
    codeVersions: string[];
    configVersions: string[];
    azureManaged: boolean;
    note?: string;
}

export interface CodePackage {
    name: string;
    version: string;
    status: string;
    hostType: string;
    processId: string;
    lastExitCode: string;
    activations: number;
    activationFailures: number;
    actions: Action[];
}

export interface DeployedPackage {
    applicationId: string;
    application: string;
    servicePackage: string;
    version: string;
    status: string;
    activationId: string;
    codePackages: CodePackage[];
}

export interface Repair {
    id: string;
    action: string;
    state: string;
    executor: string;
    nodes: string[];
    description: string;
    created?: string;
    completed?: string;
    result: string;
}

export interface Repairs {
    repairs: Repair[];
    unavailable?: string;
}

export interface LoadMetric {
    name: string;
    clusterLoad: number;
    clusterCapacity?: number;
    balanced?: boolean;
    /** Empty unless asked for with ?nodes=true (one call per node, so only on the Load tab). */
    nodes: { node: string; load: number; capacity?: number }[];
    minNode?: string;
    minLoad?: number;
    maxNode?: string;
    maxLoad?: number;
}

/** Entities of a kind per health state, from the cluster's health statistics. */
export interface HealthCount {
    kind: "Node" | "Application" | "Service" | "Partition" | "Replica";
    healthy: number;
    warning: number;
    error: number;
}

/** Everything the overview needs, from a handful of calls however big the cluster is. */
export interface Overview {
    name: string;
    endpoint: string;
    codeVersion: string;
    health: HealthReport;
    counts: HealthCount[];
    load: Omit<LoadMetric, "nodes">[];
    runningUpgrades: Upgrade[];
}

/** With the ids to open it at its place: node, application, service, partition. */
export interface SearchHit {
    kind: "node" | "application" | "service" | "partition";
    title: string;
    /** Its type, address or parent. */
    sub?: string;
    node?: string;
    applicationId?: string;
    serviceId?: string;
    partitionId?: string;
}

/** One provisioned version of an application type. */
export interface ApplicationType {
    name: string;
    version: string;
    status: string;
    statusDetails?: string;
    defaultParameters: Record<string, string>;
    /** fabric:/ names of the applications of this type and version. */
    applications: string[];
    actions: Action[];
}

export interface ServiceType {
    name: string;
    stateful: boolean;
    manifest: string;
    manifestVersion: string;
}

export interface ServiceSettingInfo {
    key: string;
    title: string;
    kind: "count" | "seconds" | "text" | "choice" | "flag";
    /** "" when the cluster default applies. */
    value: string;
    choices?: string[] | null;
    min?: number | null;
    max?: number | null;
}

export interface ServiceSettingsInfo {
    stateful: boolean;
    settings: ServiceSettingInfo[];
    /** Null where it can't be changed: a System service, or a read-only context. */
    action: Action | null;
}

export interface StoreFolder {
    path: string;
    name: string;
    fileCount?: number | null;
    size?: number | null;
    /** An application package: the type and version its manifest declares, where they can be told. */
    package?: { typeName: string | null; typeVersion: string | null; provisioned: boolean; status: string | null } | null;
    /** Service Fabric's own (Store, WindowsFabricStore): never deleted. */
    reserved: boolean;
    actions: Action[];
}

export interface StoreFile {
    path: string;
    name: string;
    size?: number | null;
    version?: string | null;
    modified?: string | null;
    actions: Action[];
}

export interface StoreListing {
    path: string;
    kind: "Native" | "FileShare" | "AzureBlob" | "Unknown";
    /** Only at the root, and only on clusters that report it. */
    usage?: StoreUsage | null;
    folders: StoreFolder[];
    files: StoreFile[];
}

export interface StoreUse {
    bytes?: number | null;
    files?: number | null;
}

export interface StoreUsage {
    diskCapacity?: number | null;
    diskAvailable?: number | null;
    metadata?: StoreUse | null;
    staging?: StoreUse | null;
    copy?: StoreUse | null;
    register?: StoreUse | null;
}
