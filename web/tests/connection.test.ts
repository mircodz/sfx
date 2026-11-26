import { expect, test } from "bun:test";
import { observe, onSwitch, show, shownConnection, requestSwitch, switchedHere } from "../src/lib/connection";

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

test("a switch this tab asked for is told apart, once", () => {
    requestSwitch("prod");
    expect(switchedHere("dev")).toBe(false);
    expect(switchedHere("prod")).toBe(false);

    requestSwitch("prod");
    expect(switchedHere("prod")).toBe(true);
    expect(switchedHere("prod")).toBe(false);
});
