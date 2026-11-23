// Syntax highlighting for manifests (XML) and raw entities (JSON), as escaped HTML.
// Small on purpose: these are the only two formats sfx shows, and they're well-formed.

const escape = (text: string) => text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

const span = (kind: string, text: string) => `<span class="tok-${kind}">${escape(text)}</span>`;

const jsonToken = /("(?:\\.|[^"\\])*")(\s*:)?|\b(true|false|null)\b|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/g;

export function highlightJson(text: string): string {
    let out = "";
    let last = 0;
    for (const m of text.matchAll(jsonToken)) {
        out += escape(text.slice(last, m.index));
        const [whole, string, colon, literal, number] = m;
        if (string) out += span(colon ? "key" : "string", string) + escape(colon ?? "");
        else if (literal) out += span("literal", literal);
        else if (number) out += span("number", number);
        else out += escape(whole);
        last = m.index! + whole.length;
    }
    return out + escape(text.slice(last));
}

const xmlToken = /(<!--[\s\S]*?-->)|(<\?[\s\S]*?\?>)|(<\/?)([\w:.-]+)([^>]*?)(\/?>)/g;
const xmlAttribute = /([\w:.-]+)(\s*=\s*)("[^"]*"|'[^']*')/g;

function attributes(text: string): string {
    let out = "";
    let last = 0;
    for (const m of text.matchAll(xmlAttribute)) {
        out += escape(text.slice(last, m.index));
        out += span("attr", m[1]) + escape(m[2]) + span("string", m[3]);
        last = m.index! + m[0].length;
    }
    return out + escape(text.slice(last));
}

export function highlightXml(text: string): string {
    let out = "";
    let last = 0;
    for (const m of text.matchAll(xmlToken)) {
        out += escape(text.slice(last, m.index));
        const [whole, comment, declaration, open, name, attrs, close] = m;
        if (comment) out += span("comment", comment);
        else if (declaration) out += span("comment", declaration);
        else out += span("punct", open) + span("tag", name) + attributes(attrs) + span("punct", close);
        last = m.index! + whole.length;
    }
    return out + escape(text.slice(last));
}

export function highlight(text: string, format: string): string | null {
    if (format === "json") return highlightJson(text);
    if (format === "xml") return highlightXml(text);
    return null;
}
