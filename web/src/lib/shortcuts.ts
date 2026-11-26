export type Shortcut = "find" | "contexts" | "filter";

type Press = { key: string; metaKey: boolean; ctrlKey: boolean; altKey: boolean; shiftKey: boolean };

export const isFindKey = (e: Press) => e.key.toLowerCase() === "k" && (e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey;

/**
 * As in the terminal: "c" opens contexts (on contexts, its filter box), "/" the page's filter box, not while typing.
 * ⌘K or ctrl+K finds anything from anywhere (ctrl+F stays the browser's own find). None of them while a dialog or a side panel is open: leaving the page
 * would throw its edits away.
 */
export function shortcutFor(e: Press, at: { typing: boolean; dialogOpen: boolean; onContexts: boolean }): Shortcut | null {
    if (at.dialogOpen) return null;
    if (isFindKey(e)) return "find";
    if (e.metaKey || e.ctrlKey || e.altKey || at.typing) return null;
    if (e.key === "c") return at.onContexts ? "filter" : "contexts";
    if (e.key === "/") return "filter";
    return null;
}
