import { expect, test } from "bun:test";
import { follow, observe, onSwitch, show, shownConnection, switchAbandoned, switchAnnounced, switchFailed, switchStarted } from "../src/lib/connection";

test("a tab hears once that sfx ui moved to another connection", () => {
    let heard = 0;
    const stop = onSwitch(() => heard++);

    observe("a");
    expect(heard).toBe(0);

    show("a");
    expect(shownConnection()).toBe("a");
    observe("a");
    observe(null);
    expect(heard).toBe(0);

    observe("b");
    observe("b");
    expect(heard).toBe(1);

    show("b");
    observe("c");
    expect(heard).toBe(2);

    stop();
    show("c");
    observe("d");
    expect(heard).toBe(2);
});

const dev = { id: "1", context: "dev" };
const prod = { id: "2", context: "prod" };
const fromAnotherTab = { notice: "Switched to prod from another tab.", overview: true };

test("the contexts page announces the switch it waits for, so the tab doesn't", () => {
    switchStarted("prod");
    expect(follow(dev, prod)).toBeUndefined();

    switchAnnounced(prod.id);
    expect(follow(dev, prod)).toBeUndefined();
    expect(follow(dev, { id: "3", context: "prod" })).toEqual(fromAnotherTab);
});

test("after a failed switch, another tab's switch to the same context is announced and opens the overview", () => {
    switchStarted("prod");
    switchFailed();

    expect(follow(dev, { id: "4", context: "prod" })).toEqual(fromAnotherTab);
});

test("a switch that completes after the contexts page was left is announced and opens the overview", () => {
    switchStarted("prod");
    switchAbandoned();

    expect(follow(dev, { id: "5", context: "prod" })).toEqual({ notice: "Switched to prod.", overview: true });
    expect(follow(dev, { id: "6", context: "prod" })).toEqual(fromAnotherTab);
});

test("a reconnect to the same context stays on the page", () => {
    expect(follow(dev, { id: "7", context: "dev" })).toEqual({ notice: "Reconnected to dev from another tab.", overview: false });
});
