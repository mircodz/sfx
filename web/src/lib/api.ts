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
    Overview,
    SearchHit,
    ApplicationType,
    ServiceType,
    ServiceSettingsInfo,
    StoreListing,
} from "./types";
import { observe, requestSwitch, shownConnection } from "./connection";

// The link sfx ui prints is /?token=…; keep the token for this tab and strip only it from the address bar. The one it
// opens is /?launch=…, a code good once for a minute that's traded for the token, as a browser's command line is
// visible to other users.
const tokenKey = "sfx-token";
const params = new URLSearchParams(location.search);
const fromUrl = params.get("token");
const launch = params.get("launch");
if (fromUrl) sessionStorage.setItem(tokenKey, fromUrl);
if (fromUrl || launch) {
    params.delete("token");
    params.delete("launch");
    const rest = params.toString();
    history.replaceState(null, "", `${location.pathname}${rest ? `?${rest}` : ""}${location.hash}`);
}

/** Why the launch code couldn't be traded, said instead of a bare 401. */
let launchError: string | undefined;
const launched = launch && !fromUrl ? trade(launch) : Promise.resolve();

async function trade(code: string) {
    try {
        const response = await fetch("/api/launch", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code }) });
        const data = await response.json().catch(() => ({}));
        if (response.ok && data.token) sessionStorage.setItem(tokenKey, data.token);
        else launchError = data.error;
    } catch {
        // Not answering: every request says so.
    }
}

export class ApiError extends Error {
    status: number;

    constructor(message: string, status: number) {
        super(message);
        this.status = status;
    }
}

const sessionHeader = "X-Sfx-Session";

/** The token, and the connection this tab shows so a change can't land on a cluster it isn't showing. */
const headers = () => ({ "X-Sfx-Token": sessionStorage.getItem(tokenKey) ?? "", [sessionHeader]: shownConnection() });

async function send<T>(path: string, init: RequestInit, failed: string): Promise<T> {
    await launched;
    let response: Response;
    try {
        response = await fetch(`/api${path}`, { ...init, headers: { ...headers(), ...init.headers } });
    } catch {
        throw new ApiError("sfx ui isn't answering. Check that it's still running.", 0);
    }
    observe(response.headers.get(sessionHeader));
    const data = await response.json().catch(() => ({}));
    if (!response.ok) {
        const message = response.status === 401 && launchError ? launchError : data.error;
        throw new ApiError(message ?? `${failed} (${response.status}).`, response.status);
    }
    return data as T;
}

/** No Content-Type: the browser sets the multipart boundary itself. */
const upload = <T>(path: string, form: FormData) => send<T>(path, { method: "POST", body: form }, "The upload failed");

const request = <T>(method: string, path: string, body?: unknown) =>
    send<T>(
        path,
        {
            method,
            headers: body === undefined ? {} : { "Content-Type": "application/json" },
            body: body === undefined ? undefined : JSON.stringify(body),
        },
        "The request failed",
    );

const get = <T>(path: string) => request<T>("GET", path);
const seg = encodeURIComponent;

export const api = {
    session: () => get<Session>("/session"),
    contexts: () => get<Contexts>("/contexts"),
    connect: (name: string) => {
        requestSwitch(name);
        return request<void>("POST", `/contexts/${seg(name)}/connect`);
    },
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
    load: (nodes = false) => get<{ metrics: LoadMetric[] }>(`/load${nodes ? "?nodes=true" : ""}`),
    overview: () => get<Overview>("/overview"),
    search: (q: string) => get<SearchHit[]>(`/search?q=${seg(q)}`),
    types: () => get<ApplicationType[]>("/types"),
    serviceTypes: (name: string, version: string) => get<ServiceType[]>(`/types/${seg(name)}/${seg(version)}/services`),
    serviceSettings: (appId: string, serviceId: string) => get<ServiceSettingsInfo>(`/applications/${seg(appId)}/services/${seg(serviceId)}/settings`),
    store: (path = "") => get<StoreListing>(`/store?path=${seg(path)}`),
    storeSize: (path: string) => get<{ size: number | null }>(`/store/size?path=${seg(path)}`),
    upload: (form: FormData) => upload<{ operationId: string; message: string }>("/store/upload", form),
    serviceReplicas: (appId: string, serviceId: string) => get<ClusterReplica[]>(`/applications/${seg(appId)}/services/${seg(serviceId)}/replicas`),
    clusterUpgradeOptions: () => get<ClusterUpgradeOptions>("/cluster/upgrade-options"),
    replicas: () => get<ClusterReplica[]>("/replicas"),
    run: (action: Action, inputs: Record<string, string>, confirm?: string) =>
        request<{ message: string }>("POST", `/actions/${seg(action.id)}`, { target: action.target, inputs, confirm, signature: action.signature }),
};
