import type { DomainState, Health, Status } from "./types";

export type Tone = "success" | "warning" | "danger" | "accent" | "neutral";

const tones: Record<Status, Tone> = {
    Healthy: "success",
    Warning: "warning",
    Error: "danger",
    Unknown: "neutral",
    Done: "success",
    Upgrading: "accent",
    Verifying: "warning",
    Pending: "neutral",
    Failed: "danger",
    "In progress": "accent",
};

export function toneOf(status: Status | DomainState): Tone {
    return tones[status] ?? "neutral";
}

export const healthFilters = ["All", "Healthy", "Warning", "Error"] as const;
export type HealthFilter = (typeof healthFilters)[number];

export function matches(filter: HealthFilter, health: Health) {
    return filter === "All" || filter === health;
}

const rank: Record<Health, number> = { Error: 0, Warning: 1, Unknown: 2, Healthy: 3 };

/** Worst first. */
export function byHealth(a: Health, b: Health) {
    return rank[a] - rank[b];
}

export function worst(healths: Health[]): Health {
    return healths.reduce<Health>((w, h) => (rank[h] < rank[w] ? h : w), "Healthy");
}

export function isUnhealthy(health: Health) {
    return health === "Warning" || health === "Error";
}
