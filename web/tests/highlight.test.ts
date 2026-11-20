import { describe, expect, test } from "bun:test";
import { highlight, highlightJson, highlightXml } from "../src/lib/highlight";

// highlight() output goes into dangerouslySetInnerHTML, so it must never let markup from the cluster through.

const textOf = (html: string) => html.replace(/<span class="tok-\w+">/g, "").replace(/<\/span>/g, "");
const unescape = (html: string) => html.replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&amp;/g, "&");

describe("highlight", () => {
    test("escapes markup inside JSON strings", () => {
        const html = highlightJson(JSON.stringify({ name: "<img src=x onerror=alert(1)>" }));
        expect(html).not.toContain("<img");
        expect(html).toContain("&lt;img");
    });

    test("escapes markup in XML text, attributes and comments", () => {
        const html = highlightXml(`<a title="<b>">x &amp; <script>alert(1)</script><!-- <i> --></a>`);
        expect(html).not.toMatch(/<(script|b|i)[\s>]/);
    });

    test("only adds its own spans", () => {
        for (const html of [highlightJson('{"a": [1, true, null, "s"]}'), highlightXml('<?xml version="1.0"?><a b="c">d</a>')]) {
            expect(html.replace(/<span class="tok-\w+">|<\/span>/g, "")).not.toMatch(/<[a-z/]/i);
        }
    });

    test("keeps the text exactly", () => {
        const json = JSON.stringify({ a: 1, b: [true, null], c: "x<y" }, null, 2);
        expect(unescape(textOf(highlightJson(json)))).toBe(json);
        const xml = `<?xml version="1.0"?>\n<!-- c -->\n<a b="1" c='2'><d/>text &amp; more</a>`;
        expect(unescape(textOf(highlightXml(xml)))).toBe(xml);
    });

    test("tells keys from string values", () => {
        const html = highlightJson('{"key": "value"}');
        expect(html).toContain('<span class="tok-key">&quot;key&quot;</span>'.replace(/&quot;/g, '"'));
        expect(html).toContain('<span class="tok-string">"value"</span>');
    });

    test("leaves other formats alone", () => {
        expect(highlight("plain", "none")).toBeNull();
    });
});
