export function plural(n: number, one: string, many = `${one}s`) {
    return `${n.toLocaleString()} ${n === 1 ? one : many}`;
}

export function count<T>(items: T[], pred: (t: T) => boolean) {
    return items.filter(pred).length;
}

/** Whole numbers as they are; fractions with enough digits that a small load doesn't read as 0. */
/** A load metric as people say it: the built-in ones without their "servicefabric:/_" prefix. */
export function metricName(name: string) {
    return name.replace(/^servicefabric:\/_/, "");
}

export function number(n: number) {
    if (Number.isInteger(n)) return n.toLocaleString();
    return n.toLocaleString(undefined, Math.abs(n) < 1 ? { maximumSignificantDigits: 2 } : { maximumFractionDigits: 1 });
}

/** Natural order for names with numbers in them: _Node_2 before _Node_10. */
export const natural = (a: string, b: string) => a.localeCompare(b, undefined, { numeric: true, sensitivity: "base" });

export function duration(seconds: number) {
    const m = Math.floor(seconds / 60);
    const h = Math.floor(m / 60);
    const d = Math.floor(h / 24);
    if (d > 0) return h % 24 ? `${plural(d, "day")} ${h % 24}h` : plural(d, "day");
    if (h > 0) return m % 60 ? `${h}h ${m % 60}m` : `${h}h`;
    if (m > 0) return `${m}m`;
    return `${Math.floor(seconds)}s`;
}

export function ago(iso: string) {
    const seconds = (Date.now() - new Date(iso).getTime()) / 1000;
    return seconds < 60 ? "just now" : `${duration(seconds)} ago`;
}

export function clock(date: Date) {
    return date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

/** "14:02" today, "19 Nov 14:02" before. */
export function when(iso: string) {
    const d = new Date(iso);
    const time = d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
    return isToday(d) ? time : `${d.toLocaleDateString([], { day: "numeric", month: "short" })} ${time}`;
}

/** "Today", "Yesterday", "18 Nov": headings for a timeline. */
export function day(iso: string) {
    const d = new Date(iso);
    if (isToday(d)) return "Today";
    const yesterday = new Date();
    yesterday.setDate(yesterday.getDate() - 1);
    if (d.toDateString() === yesterday.toDateString()) return "Yesterday";
    return d.toLocaleDateString([], { day: "numeric", month: "short", year: d.getFullYear() === new Date().getFullYear() ? undefined : "numeric" });
}

function isToday(d: Date) {
    return d.toDateString() === new Date().toDateString();
}

/** A child's name without its parent's: "fabric:/orders/Counter" under "fabric:/orders" is "Counter". */
export function shortName(name: string, parent: string) {
    return name.startsWith(parent + "/") ? name.slice(parent.length + 1) : name;
}

/** First letter lowercased unless it's an acronym or a name: "Restart" + "Replica 12" reads "Restart replica 12". */
export function lowerFirst(text: string) {
    return /^[A-Z][a-z]/.test(text) ? text[0].toLowerCase() + text.slice(1) : text;
}

/** A partition's key: "keys 0 to 9", "named west", or the scheme alone for a singleton. */
export function partitionKey(p: { scheme: string; lowKey?: string | null; highKey?: string | null; name?: string | null }) {
    if (p.name) return `named ${p.name}`;
    if (p.lowKey != null && p.highKey != null) return p.lowKey === p.highKey ? `key ${p.lowKey}` : `keys ${p.lowKey} to ${p.highKey}`;
    return p.scheme;
}
