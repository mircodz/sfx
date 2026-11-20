import { describe, expect, test } from "bun:test";
import { duration, lowerFirst, plural, shortName } from "../src/lib/format";
import { byHealth, isUnhealthy, matches, toneOf, worst } from "../src/lib/health";
import { appIdOfService, idOf } from "../src/lib/router";
import type { Upgrade } from "../src/lib/types";
import { domainState, isMonitored, upgradeProgress, upgradeWord } from "../src/lib/upgrades";

describe("format", () => {
    test("plural", () => {
        expect(plural(1, "node")).toBe("1 node");
        expect(plural(2, "entity", "entities")).toBe("2 entities");
        expect(plural(0, "replica")).toBe("0 replicas");
    });

    test("duration picks the largest unit that fits", () => {
        expect(duration(30)).toBe("30s");
        expect(duration(125)).toBe("2m");
        expect(duration(3_900)).toBe("1h 5m");
        expect(duration(7_200)).toBe("2h");
        expect(duration(86_400 * 3)).toBe("3 days");
    });

    test("shortName drops the parent", () => {
        expect(shortName("fabric:/orders/Counter", "fabric:/orders")).toBe("Counter");
        expect(shortName("fabric:/other/Counter", "fabric:/orders")).toBe("fabric:/other/Counter");
    });

    test("lowerFirst leaves names and acronyms alone", () => {
        expect(lowerFirst("Replica 12 on _Node_1")).toBe("replica 12 on _Node_1");
        expect(lowerFirst("UD 2")).toBe("UD 2");
        expect(lowerFirst("_Node_1")).toBe("_Node_1");
        expect(lowerFirst("fabric:/orders")).toBe("fabric:/orders");
    });
});

describe("health", () => {
    test("every status has a tone", () => {
        expect(toneOf("Error")).toBe("danger");
        expect(toneOf("Unknown")).toBe("neutral");
        expect(toneOf("Verifying")).toBe("warning");
    });

    test("worst and ordering", () => {
        expect(worst(["Healthy", "Warning", "Healthy"])).toBe("Warning");
        expect(worst([])).toBe("Healthy");
        expect((["Healthy", "Error", "Warning"] as const).slice().sort(byHealth)).toEqual(["Error", "Warning", "Healthy"]);
    });

    test("filters", () => {
        expect(matches("All", "Error")).toBe(true);
        expect(matches("Warning", "Error")).toBe(false);
        expect(isUnhealthy("Unknown")).toBe(false);
    });
});

describe("router", () => {
    test("ids from names", () => {
        expect(idOf("fabric:/shop/orders")).toBe("shop~orders");
        expect(appIdOfService("shop~orders~Counter")).toBe("shop~orders");
    });
});

describe("upgrades", () => {
    const base: Upgrade = {
        name: "fabric:/orders",
        kind: "application",
        from: "1.0.0",
        target: "1.1.0",
        state: "RollingForwardInProgress",
        mode: "Monitored",
        currentDomain: "1",
        nextDomain: "2",
        domains: [
            { name: "0", state: "Completed" },
            { name: "1", state: "InProgress" },
            { name: "2", state: "Pending" },
        ],
        nodes: [],
        failureReason: "None",
        healthCheckPhase: "",
        evaluations: [],
        actions: [],
    };

    test("words for the overall state", () => {
        expect(upgradeWord(base)).toBe("In progress");
        expect(upgradeWord({ ...base, state: "RollingForwardPending" })).toBe("Paused");
        expect(upgradeWord({ ...base, state: "RollingBackCompleted" })).toBe("Rolled back");
    });

    test("domain states", () => {
        expect(domainState(base, base.domains[0])).toBe("Done");
        expect(domainState(base, base.domains[1])).toBe("Upgrading");
        expect(domainState({ ...base, healthCheckPhase: "StableDuration" }, base.domains[1])).toBe("Verifying");
        expect(domainState({ ...base, state: "Failed" }, base.domains[1])).toBe("Failed");
        expect(domainState(base, base.domains[2])).toBe("Pending");
    });

    test("progress", () => {
        expect(upgradeProgress(base)).toEqual({ done: 1, total: 3, pct: 33 });
    });

    test("monitored modes", () => {
        expect(isMonitored("monitored-manual")).toBe(true);
        expect(isMonitored("unmonitored-auto")).toBe(false);
    });
});
