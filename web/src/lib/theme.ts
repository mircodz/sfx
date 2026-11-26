export type Theme = "system" | "light" | "dark";

const key = "sfx-theme";
export const themes: Theme[] = ["system", "light", "dark"];

export function storedTheme(): Theme {
    const t = localStorage.getItem(key);
    return t === "light" || t === "dark" ? t : "system";
}

/** "system" removes data-theme so prefers-color-scheme decides. */
export function applyTheme(theme: Theme) {
    if (theme === "system") {
        delete document.documentElement.dataset.theme;
        localStorage.removeItem(key);
    } else {
        document.documentElement.dataset.theme = theme;
        localStorage.setItem(key, theme);
    }
}
