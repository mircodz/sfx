import { describe, expect, test } from "bun:test";
import { isFindKey, shortcutFor } from "../src/lib/shortcuts";

const key = (k: string, mods: { meta?: boolean; ctrl?: boolean; alt?: boolean; shift?: boolean } = {}) => ({
    key: k,
    metaKey: !!mods.meta,
    ctrlKey: !!mods.ctrl,
    altKey: !!mods.alt,
    shiftKey: !!mods.shift,
});
const page = { typing: false, dialogOpen: false, onContexts: false };

describe("shortcuts", () => {
    test("⌘K and ctrl+K find, from anywhere on the page", () => {
        expect(shortcutFor(key("k", { meta: true }), page)).toBe("find");
        expect(shortcutFor(key("K", { ctrl: true }), { ...page, typing: true })).toBe("find");
        expect(shortcutFor(key("k", { meta: true, shift: true }), page)).toBeNull();
        expect(isFindKey(key("k", { ctrl: true }))).toBe(true);
        expect(isFindKey(key("k"))).toBe(false);
    });

    test("letters are left alone while typing", () => {
        expect(shortcutFor(key("c"), page)).toBe("contexts");
        expect(shortcutFor(key("c"), { ...page, onContexts: true })).toBe("filter");
        expect(shortcutFor(key("/"), page)).toBe("filter");
        expect(shortcutFor(key("c"), { ...page, typing: true })).toBeNull();
        expect(shortcutFor(key("/", { ctrl: true }), page)).toBeNull();
    });

    test("nothing fires behind a dialog or side panel", () => {
        const open = { ...page, dialogOpen: true };
        expect(shortcutFor(key("k", { meta: true }), open)).toBeNull();
        expect(shortcutFor(key("k", { ctrl: true }), open)).toBeNull();
        expect(shortcutFor(key("c"), open)).toBeNull();
        expect(shortcutFor(key("/"), open)).toBeNull();
    });
});
