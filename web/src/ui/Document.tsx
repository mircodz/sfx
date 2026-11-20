import { useMemo, useState } from "react";
import { highlight } from "../lib/highlight";
import { useResource } from "../lib/resource";
import type { Text } from "../lib/types";
import { IconButton } from "./Button";
import { Load } from "./Load";
import "./Document.css";

/** Saves text as a file. The object URL lives only for the click, so nothing piles up while the page refreshes. */
function download(text: string, filename: string) {
    const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 0);
}

export function Code({ text, format, wrap }: { text: string; format: string; wrap: boolean }) {
    // Highlighting a large manifest takes a moment: once per document, not on every render.
    const html = useMemo(() => highlight(text, format), [text, format]);
    const className = `document-code ${wrap ? "wrap" : ""}`;
    return html === null ? (
        <pre className={className}>{text}</pre>
    ) : (
        // highlight() escapes all of the text and only adds its own <span>s.
        <pre className={className} dangerouslySetInnerHTML={{ __html: html }} />
    );
}

/** A manifest or raw entity, loaded when shown, with copy, download and line wrapping. */
export function Document({ cacheKey, load, name }: { cacheKey: string; load: () => Promise<Text>; name: string }) {
    const doc = useResource(cacheKey, load, false);
    const [copied, setCopied] = useState(false);
    const [wrap, setWrap] = useState(false);
    return (
        <Load resource={doc} shape="lines">
            {(d) => (
                <div className="document">
                    <div className="document-toolbar">
                        <span className="secondary">{d.format === "none" ? "Text" : d.format.toUpperCase()}</span>
                        <span className="spacer" />
                        <button type="button" className="chip" aria-pressed={wrap} onClick={() => setWrap(!wrap)}>
                            Wrap lines
                        </button>
                        <IconButton
                            icon={copied ? "check" : "copy"}
                            label={copied ? "Copied" : "Copy"}
                            onClick={() => navigator.clipboard.writeText(d.text).then(() => setCopied(true))}
                        />
                        <IconButton icon="download" label="Download" onClick={() => download(d.text, `${name}.${d.format === "none" ? "txt" : d.format}`)} />
                    </div>
                    <Code text={d.text} format={d.format} wrap={wrap} />
                </div>
            )}
        </Load>
    );
}
