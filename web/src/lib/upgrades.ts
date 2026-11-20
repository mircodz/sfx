import type { DomainState, Upgrade } from "./types";

export type UpgradeWord = "In progress" | "Paused" | "Complete" | "Rolled back" | "Failed";

export function upgradeWord(u: Upgrade): UpgradeWord {
    switch (u.state) {
        case "RollingForwardPending":
            return "Paused";
        case "RollingForwardCompleted":
            return "Complete";
        case "RollingBackCompleted":
            return "Rolled back";
        case "Failed":
            return "Failed";
        default:
            return "In progress";
    }
}

export function isRunning(u: Upgrade) {
    return ["In progress", "Paused", "Failed"].includes(upgradeWord(u));
}

export function domainState(u: Upgrade, domain: { name: string; state: string }): DomainState {
    if (domain.state === "Completed") return "Done";
    if (domain.state === "InProgress" || domain.name === u.currentDomain) {
        if (u.state === "Failed") return "Failed";
        return ["WaitDuration", "StableDuration", "Retry"].includes(u.healthCheckPhase) ? "Verifying" : "Upgrading";
    }
    return "Pending";
}

export function upgradeProgress(u: Upgrade) {
    const done = u.domains.filter((d) => d.state === "Completed").length;
    const total = u.domains.length;
    return { done, total, pct: total ? Math.round((done / total) * 100) : 0 };
}

export function modeText(mode: string) {
    return ({ Monitored: "Monitored", UnmonitoredAuto: "Unmonitored, automatic", UnmonitoredManual: "Unmonitored, manual" } as Record<string, string>)[mode] ?? mode;
}

/** What each upgrade mode does, for the review step. Keys are the server's mode choices. */
export const modeEffects: Record<string, string> = {
    "monitored-rollback": "One upgrade domain at a time, checking health after each; rolls back on its own if health fails.",
    "monitored-manual": "One upgrade domain at a time, checking health after each; stops for you to decide if health fails.",
    "unmonitored-auto": "One upgrade domain at a time without checking health. Use it only on test clusters.",
    "unmonitored-manual": "One upgrade domain, then waits for you to resume it into the next.",
};

export const isMonitored = (mode: string) => mode.startsWith("monitored");
