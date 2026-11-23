// The contexts this browser switched to last, newest first, for the top of the Contexts page.

const key = "sfx-recent-contexts";
const keep = 5;

export function recentContexts(): string[] {
    try {
        const list = JSON.parse(localStorage.getItem(key) ?? "[]");
        return Array.isArray(list) ? list.filter((x) => typeof x === "string") : [];
    } catch {
        return [];
    }
}

export function rememberContext(name: string) {
    localStorage.setItem(key, JSON.stringify([name, ...recentContexts().filter((n) => n !== name)].slice(0, keep)));
}
