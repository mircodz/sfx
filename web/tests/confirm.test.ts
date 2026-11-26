import { describe, expect, mock, test } from "bun:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { canConfirm } from "../src/actions/confirm";
import type { Action, ActionInput } from "../src/lib/types";

// The real client reads the page's address when it loads.
mock.module("../src/lib/api", () => ({ api: {} }));
const { ConfirmAction } = await import("../src/actions/ConfirmAction");

const action = (risk: Action["risk"], confirmText?: string, inputs: ActionInput[] = []): Action => ({
    id: "node.restart",
    title: "Restart",
    risk,
    confirmText,
    target: { node: "_Node_1" },
    inputs,
    signature: "",
    form: false,
});

const render = (a: Action) => renderToStaticMarkup(createElement(ConfirmAction, { action: a, onClose: () => {} }));
const autofocused = (html: string) => html.match(/<(\w+)[^>]*data-autofocus[^>]*>([^<]*)/)?.slice(1);

describe("confirm dialog", () => {
    test("typed confirmation must match exactly", () => {
        expect(canConfirm(action("destructive", "_Node_1"), "")).toBe(false);
        expect(canConfirm(action("destructive", "_Node_1"), "_node_1")).toBe(false);
        expect(canConfirm(action("destructive", "_Node_1"), "_Node_1 ")).toBe(false);
        expect(canConfirm(action("destructive", "_Node_1"), "_Node_1")).toBe(true);
        expect(canConfirm(action("destructive"), "")).toBe(true);
        expect(canConfirm(action("confirm"), "")).toBe(true);
    });

    test("focus starts on Cancel, not on the action", () => {
        expect(autofocused(render(action("confirm")))).toEqual(["button", "Cancel"]);
        expect(autofocused(render(action("destructive")))).toEqual(["button", "Cancel"]);
        const withInput = render(action("confirm", undefined, [{ name: "reason", label: "Reason", kind: "text" }]));
        expect(autofocused(withInput)).toEqual(["button", "Cancel"]);
    });

    test("with a name to type, focus starts in its box and the action stays disabled", () => {
        const html = render(action("destructive", "_Node_1"));
        expect(autofocused(html)?.[0]).toBe("input");
        expect(html.match(/data-autofocus/g)).toHaveLength(1);
        expect(html).toMatch(/<button type="submit"[^>]*disabled=""[^>]*>Restart</);
    });
});
