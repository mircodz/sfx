import { describe, expect, test } from "bun:test";
import { renderToStaticMarkup } from "react-dom/server";
import { createElement } from "react";
import { Evaluations } from "../src/ui/Health";
import type { Evaluation } from "../src/lib/types";

const e = (depth: number, description: string): Evaluation => ({ depth, kind: "x", health: "Error", description });

describe("Evaluations", () => {
    test("shows 5 siblings, then 'Show N more', and hides the children of hidden ones", () => {
        const list = [e(0, "root"), ...Array.from({ length: 8 }, (_, i) => [e(1, `child ${i}`), e(2, `grandchild ${i}`)]).flat()];
        const html = renderToStaticMarkup(createElement(Evaluations, { evaluations: list }));
        expect(html).toContain("child 4");
        expect(html).toContain("grandchild 4");
        expect(html).not.toContain("child 5");
        expect(html).not.toContain("grandchild 7");
        expect(html).toContain("Show 3 more");
    });

    test("five or fewer siblings are all shown", () => {
        const html = renderToStaticMarkup(createElement(Evaluations, { evaluations: [e(0, "a"), e(0, "b"), e(0, "c")] }));
        expect(html).toContain(">c<");
        expect(html).not.toContain("more");
    });
});
