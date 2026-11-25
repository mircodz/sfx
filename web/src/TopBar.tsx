import { useContext, useEffect, useRef, useState } from "react";
import { api } from "./lib/api";
import { clock } from "./lib/format";
import { useNotice } from "./lib/notice";
import { usePageRefresh } from "./lib/pageRefresh";
import { RefreshContext } from "./lib/resource";
import { applyTheme, storedTheme } from "./lib/theme";
import type { Theme } from "./lib/theme";
import type { Session } from "./lib/types";
import { ContextLabel } from "./ui/ContextLabel";
import { Icon } from "./ui/Icon";
import "./TopBar.css";

export function TopBar({ session, onSessionChange, onFind, contextsOpen }: { session?: Session; onSessionChange: () => void; onFind: () => void; contextsOpen: boolean }) {
    const mac = /Mac|iPhone|iPad/.test(navigator.platform);
    return (
        <header className="topbar">
            <a className="topbar-brand" href="#/" aria-label="sfx, overview" title={session ? `sfx ${session.version}` : "sfx"}>
                sfx
            </a>
            {session && (
                <>
                    <a className="topbar-context" href="#/contexts" aria-current={contextsOpen ? "page" : undefined} title={`Switch context (c): ${session.context}`}>
                        <Icon name="server-2" className="topbar-context-icon" />
                        {session.folder && <span className="topbar-context-folder">{session.folder.split("/").join(" / ")} /</span>}
                        <span className="topbar-context-name">{session.context}</span>
                        <ContextLabel label={session.label} color={session.color} />
                        <Icon name="selector" className="topbar-context-chevron" />
                    </a>
                    {session.readOnly ? <ReadOnly /> : <PowerSwitch on={session.power} onChange={onSessionChange} />}
                    <button type="button" className="topbar-find" onClick={onFind} title={`Find a node, application, service or partition (${mac ? "⌘K" : "ctrl+K"})`}>
                        <Icon name="search" />
                        <span className="topbar-find-text">Find a node, application, service or partition</span>
                        <kbd>{mac ? "⌘K" : "ctrl K"}</kbd>
                    </button>
                </>
            )}
            <span className="topbar-spacer" />
            <Refresh />
            <Settings version={session?.version} />
        </header>
    );
}

function ReadOnly() {
    return (
        <span className="topbar-readonly" title="This context is read-only: nothing here can change the cluster.">
            <Icon name="lock" />
            <span>Read-only</span>
        </span>
    );
}

/** Unlocks the riskiest actions; amber while on, so a risky mode is never subtle. */
function PowerSwitch({ on, onChange }: { on: boolean; onChange: () => void }) {
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
            role="switch"
            aria-checked={on}
            className={`topbar-power ${on ? "on" : ""}`}
            onClick={toggle}
            title="Also offer the riskiest actions: recover from quorum loss, force-remove, delete, remove node state."
        >
            <Icon name={on ? "bolt" : "bolt-off"} />
            <span className="topbar-power-text">Power actions</span>
            <span className="topbar-switch" aria-hidden="true" />
        </button>
    );
}

/** Hidden on pages that don't refresh. */
function Refresh() {
    const page = usePageRefresh();
    if (!page) return null;
    return (
        <>
            {page.at && <span className="topbar-updated">Updated {clock(page.at)}</span>}
            <button type="button" className="topbar-icon" onClick={page.reload} title="Refresh" aria-label={page.at ? `Refresh (updated ${clock(page.at)})` : "Refresh"}>
                <Icon name="refresh" />
            </button>
        </>
    );
}

const themeLabels: Record<Theme, string> = { system: "System", light: "Light", dark: "Dark" };

function Settings({ version }: { version?: string }) {
    const [open, setOpen] = useState(false);
    const [theme, setTheme] = useState<Theme>(storedTheme);
    const anchor = useRef<HTMLDivElement>(null);

    useEffect(() => {
        if (!open) return;
        const close = (e: MouseEvent) => !anchor.current?.contains(e.target as Node) && setOpen(false);
        const escape = (e: KeyboardEvent) => {
            if (e.key !== "Escape") return;
            setOpen(false);
            anchor.current?.querySelector<HTMLButtonElement>("button")?.focus();
        };
        document.addEventListener("mousedown", close);
        document.addEventListener("keydown", escape);
        return () => {
            document.removeEventListener("mousedown", close);
            document.removeEventListener("keydown", escape);
        };
    }, [open]);

    return (
        <div className="topbar-settings" ref={anchor}>
            <button type="button" className="topbar-icon" aria-haspopup="dialog" aria-expanded={open} title="Settings" aria-label="Settings" onClick={() => setOpen(!open)}>
                <Icon name="settings" />
            </button>
            {open && (
                <div className="topbar-menu" role="dialog" aria-label="Settings">
                    <div className="topbar-menu-label" id="theme-label">
                        Theme
                    </div>
                    <div className="topbar-segments" role="radiogroup" aria-labelledby="theme-label">
                        {(Object.keys(themeLabels) as Theme[]).map((t) => (
                            <button
                                key={t}
                                type="button"
                                role="radio"
                                aria-checked={theme === t}
                                onClick={() => {
                                    applyTheme(t);
                                    setTheme(t);
                                }}
                            >
                                {themeLabels[t]}
                            </button>
                        ))}
                    </div>
                    {version && <div className="topbar-menu-foot">sfx {version}</div>}
                </div>
            )}
        </div>
    );
}
