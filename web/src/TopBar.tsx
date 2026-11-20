import { useContext, useState } from "react";
import { api } from "./lib/api";
import { useNotice } from "./lib/notice";
import { RefreshContext } from "./lib/resource";
import { applyTheme, storedTheme, themes } from "./lib/theme";
import type { Theme } from "./lib/theme";
import type { Session } from "./lib/types";
import { IconButton } from "./ui/Button";
import { ContextLabel } from "./ui/ContextLabel";
import { Icon } from "./ui/Icon";
import { useOperations } from "./lib/operations";
import "./TopBar.css";

const pages = [
    { path: "", label: "Overview" },
    { path: "nodes", label: "Nodes" },
    { path: "apps", label: "Applications" },
    { path: "replicas", label: "Replicas" },
    { path: "upgrades", label: "Upgrades" },
    { path: "events", label: "Events" },
    { path: "activity", label: "Activity" },
];

/** The one bar on every page: the context, the pages, the power mode and the theme. */
export function TopBar({ session, current, onSessionChange }: { session?: Session; current: string; onSessionChange: () => void }) {
    return (
        <header className="topbar">
            <a className="brand" href="#/">
                sfx
            </a>
            {session && <ContextSwitch session={session} />}
            <nav className="topbar-nav" aria-label="Pages">
                {pages.filter((p) => p.path !== "events" || session?.eventStore !== false).map((p) => (
                    <a key={p.path} className="chip" href={`#/${p.path}`} aria-current={p.path === current ? "page" : undefined}>
                        {p.label}
                    </a>
                ))}
            </nav>
            <div className="topbar-end">
                <RunningOperations />
                {session && (session.readOnly ? <span className="pill tone-neutral">Read-only</span> : <PowerToggle on={session.power} onChange={onSessionChange} />)}
                <ThemeButton />
            </div>
        </header>
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
            className="chip power"
            aria-pressed={on}
            onClick={toggle}
            title="Also offer the riskiest actions: recover from quorum loss, force-remove, delete, remove node state."
        >
            Power actions
        </button>
    );
}

const themeIcons: Record<Theme, string> = { system: "device-desktop", light: "sun", dark: "moon" };

function ThemeButton() {
    const [theme, setTheme] = useState<Theme>(storedTheme);
    const next = themes[(themes.indexOf(theme) + 1) % themes.length];
    return (
        <IconButton
            icon={themeIcons[theme]}
            label={`Theme: ${theme}. Switch to ${next}.`}
            onClick={() => {
                applyTheme(next);
                setTheme(next);
            }}
        />
    );
}

/** The connected context, with its folder and label; opens the Contexts page (also "c"). */
function ContextSwitch({ session }: { session: Session }) {
    const folder = session.folder?.split("/").join(" / ");
    return (
        <a className="context-switch" href="#/contexts" title="Switch context (c)">
            {folder && <span className="context-switch-folder">{folder} /</span>}
            <span className="context-switch-name">{session.context}</span>
            <ContextLabel label={session.label} color={session.color} />
            <Icon name="selector" className="context-switch-icon" />
        </a>
    );
}

/** "2 running" while changes are under way; opens the activity, where they're listed. */
function RunningOperations() {
    const running = useOperations().filter((o) => o.state === "running");
    if (running.length === 0) return null;
    return (
        <a className="chip running" href="#/activity" title={running.map((o) => o.title).join("\n")}>
            <span className="running-dot" aria-hidden="true" />
            {running.length} running
        </a>
    );
}
