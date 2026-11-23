import { useContext, useState } from "react";
import { api } from "./lib/api";
import { useNotice } from "./lib/notice";
import { useOperations } from "./lib/operations";
import { RefreshContext } from "./lib/resource";
import { applyTheme, storedTheme, themes } from "./lib/theme";
import type { Theme } from "./lib/theme";
import type { Session } from "./lib/types";
import { ContextLabel } from "./ui/ContextLabel";
import { Icon } from "./ui/Icon";
import "./Sidebar.css";

const pages = [
    { path: "", label: "Overview", icon: "layout-dashboard" },
    { path: "nodes", label: "Nodes", icon: "server" },
    { path: "apps", label: "Applications", icon: "apps" },
    { path: "replicas", label: "Replicas", icon: "stack-2" },
    { path: "upgrades", label: "Upgrades", icon: "arrow-big-up-lines" },
    { path: "events", label: "Events", icon: "timeline-event" },
    { path: "activity", label: "Activity", icon: "history" },
];

/**
 * The one navigation on every page: which cluster (and switching it), the pages, what's running, and at the bottom
 * the power mode and theme. Icons only on narrower windows; a strip across the top on a phone.
 */
export function Sidebar({ session, current, onSessionChange }: { session?: Session; current: string; onSessionChange: () => void }) {
    const running = useOperations().filter((o) => o.state === "running");
    return (
        <aside className="sidebar">
            <a className="sidebar-brand" href="#/" aria-label="sfx, overview">
                sfx
            </a>
            {session && <ContextBlock session={session} active={current === "contexts"} />}
            <nav className="sidebar-nav" aria-label="Pages">
                {pages
                    .filter((p) => p.path !== "events" || session?.eventStore !== false)
                    .map((p) => (
                        <a key={p.path} className="sidebar-link" href={`#/${p.path}`} aria-current={p.path === current ? "page" : undefined} title={p.label}>
                            <Icon name={p.icon} className="sidebar-icon" />
                            <span className="sidebar-label">{p.label}</span>
                            {p.path === "activity" && running.length > 0 && (
                                <span className="sidebar-badge" title={running.map((o) => o.title).join("\n")}>
                                    {running.length}
                                    <span className="sr-only"> running</span>
                                </span>
                            )}
                        </a>
                    ))}
            </nav>
            <div className="sidebar-end">
                {session && (session.readOnly ? <ReadOnly /> : <PowerToggle on={session.power} onChange={onSessionChange} />)}
                <ThemeButton />
                {session && <span className="sidebar-version sidebar-label">sfx {session.version}</span>}
            </div>
        </aside>
    );
}

/** The connected context with its folder and label; opens the Contexts page (also "c"). */
function ContextBlock({ session, active }: { session: Session; active: boolean }) {
    return (
        <a className="sidebar-context" href="#/contexts" aria-current={active ? "page" : undefined} title={`Switch context (c): ${session.context}`}>
            <Icon name="server-2" className="sidebar-icon" />
            <span className="sidebar-label sidebar-context-text">
                {session.folder && <span className="sidebar-context-folder">{session.folder.split("/").join(" / ")}</span>}
                <span className="sidebar-context-name">
                    {session.context}
                    <ContextLabel label={session.label} color={session.color} />
                </span>
            </span>
            <Icon name="selector" className="sidebar-context-chevron sidebar-label" />
        </a>
    );
}

function ReadOnly() {
    return (
        <span className="sidebar-link sidebar-static" title="This context is read-only: nothing here can change the cluster.">
            <Icon name="lock" className="sidebar-icon" />
            <span className="sidebar-label">Read-only</span>
        </span>
    );
}

/** Unlocks the riskiest actions; amber while on, so a risky mode is never subtle. */
function PowerToggle({ on, onChange }: { on: boolean; onChange: () => void }) {
    const { bump } = useContext(RefreshContext);
    const notify = useNotice();
    const toggle = async () => {
        try {
            await api.setPower(!on);
            onChange();
            bump();
        } catch (e) {
            notify(e instanceof Error ? e.message : String(e), false);
        }
    };
    return (
        <button
            type="button"
            className="sidebar-link sidebar-power"
            aria-pressed={on}
            onClick={toggle}
            title="Also offer the riskiest actions: recover from quorum loss, force-remove, delete, remove node state."
        >
            <Icon name={on ? "bolt" : "bolt-off"} className="sidebar-icon" />
            <span className="sidebar-label">{on ? "Power actions on" : "Power actions"}</span>
        </button>
    );
}

const themeIcons: Record<Theme, string> = { system: "device-desktop", light: "sun", dark: "moon" };
const themeNames: Record<Theme, string> = { system: "System theme", light: "Light theme", dark: "Dark theme" };

function ThemeButton() {
    const [theme, setTheme] = useState<Theme>(storedTheme);
    const next = themes[(themes.indexOf(theme) + 1) % themes.length];
    return (
        <button
            type="button"
            className="sidebar-link"
            aria-label={`${themeNames[theme]}. Switch to ${next}.`}
            title={`${themeNames[theme]}; click for ${next}`}
            onClick={() => {
                applyTheme(next);
                setTheme(next);
            }}
        >
            <Icon name={themeIcons[theme]} className="sidebar-icon" />
            <span className="sidebar-label">{themeNames[theme]}</span>
        </button>
    );
}
