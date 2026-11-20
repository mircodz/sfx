import type {
    Action,
    Activity,
    Application,
    ApplicationDetail,
    Cluster,
    Events,
    Node,
    NodeDetail,
    PartitionDetail,
    ServiceDetail,
    Session,
    Text,
    Upgrade,
    ClusterReplica,
    Contexts,
    Operation,
    ClusterUpgradeOptions,
    DeployedPackage,
    Repairs,
    LoadMetric,
} from "./types";

// sfx ui opens the page as /?token=…; keep the token for this tab and take it out of the address bar.
const tokenKey = "sfx-token";
const fromUrl = new URLSearchParams(location.search).get("token");
if (fromUrl) {
    sessionStorage.setItem(tokenKey, fromUrl);
    history.replaceState(null, "", location.pathname + location.hash);
}

export class ApiError extends Error {
    status: number;

    constructor(message: string, status: number) {
        super(message);
        this.status = status;
    }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
    let response: Response;
    try {
        response = await fetch(`/api${path}`, {
            method,
            headers: {
                "X-Sfx-Token": sessionStorage.getItem(tokenKey) ?? "",
                ...(body === undefined ? {} : { "Content-Type": "application/json" }),
            },
            body: body === undefined ? undefined : JSON.stringify(body),
        });
    } catch {
        throw new ApiError("sfx ui isn't answering. Check that it's still running.", 0);
    }
    const data = await response.json().catch(() => ({}));
    if (!response.ok) {
        throw new ApiError(data.error ?? `The request failed (${response.status}).`, response.status);
    }
    return data as T;
}

const get = <T>(path: string) => request<T>("GET", path);
const seg = encodeURIComponent;

export const api = {
    session: () => get<Session>("/session"),
    contexts: () => get<Contexts>("/contexts"),
    connect: (name: string) => request<void>("POST", `/contexts/${seg(name)}/connect`),
    dismissConnect: () => request<void>("POST", "/contexts/dismiss"),
    setPower: (on: boolean) => request<Session>("POST", "/session/power", { on }),
    cluster: () => get<Cluster>("/cluster"),
    clusterManifest: () => get<Text>("/cluster/manifest"),
    nodes: () => get<Node[]>("/nodes"),
    node: (name: string) => get<NodeDetail>(`/nodes/${seg(name)}`),
    applications: () => get<Application[]>("/applications"),
    application: (id: string) => get<ApplicationDetail>(`/applications/${seg(id)}`),
    applicationManifest: (id: string) => get<Text>(`/applications/${seg(id)}/manifest`),
    service: (appId: string, id: string) => get<ServiceDetail>(`/applications/${seg(appId)}/services/${seg(id)}`),
    serviceManifest: (appId: string, id: string) => get<Text>(`/applications/${seg(appId)}/services/${seg(id)}/manifest`),
    partition: (appId: string, serviceId: string, id: string) =>
        get<PartitionDetail>(`/applications/${seg(appId)}/services/${seg(serviceId)}/partitions/${seg(id)}`),
    upgrades: () => get<Upgrade[]>("/upgrades"),
    events: (hours = 1, entity?: string) => get<Events>(`/events?hours=${hours}${entity ? `&entity=${seg(entity)}` : ""}`),
    nodeRaw: (name: string) => get<Text>(`/nodes/${seg(name)}/raw`),
    applicationRaw: (id: string) => get<Text>(`/applications/${seg(id)}/raw`),
    serviceRaw: (appId: string, id: string) => get<Text>(`/applications/${seg(appId)}/services/${seg(id)}/raw`),
    partitionRaw: (appId: string, serviceId: string, id: string) =>
        get<Text>(`/applications/${seg(appId)}/services/${seg(serviceId)}/partitions/${seg(id)}/raw`),
    activity: () => get<Activity[]>("/activity"),
    operations: () => get<Operation[]>("/operations"),
    packages: (node: string) => get<DeployedPackage[]>(`/nodes/${seg(node)}/packages`),
    repairs: () => get<Repairs>("/repairs"),
    load: () => get<{ metrics: LoadMetric[] }>("/load"),
    clusterUpgradeOptions: () => get<ClusterUpgradeOptions>("/cluster/upgrade-options"),
    replicas: () => get<ClusterReplica[]>("/replicas"),
    run: (action: Action, inputs: Record<string, string>, confirm?: string) =>
        request<{ message: string }>("POST", `/actions/${seg(action.id)}`, { target: action.target, inputs, confirm, signature: action.signature }),
};
