import { useOperations } from "./lib/operations";
import type { Session } from "./lib/types";
import { Icon } from "./ui/Icon";
import "./Sidebar.css";

const pages = [
    { path: "", label: "Overview", icon: "layout-dashboard" },
    { path: "nodes", label: "Nodes", icon: "server" },
    { path: "apps", label: "Applications", icon: "apps" },
    { path: "types", label: "Types", icon: "package" },
    { path: "replicas", label: "Replicas", icon: "stack-2" },
    { path: "upgrades", label: "Upgrades", icon: "arrow-big-up-lines" },
    { path: "store", label: "Image store", icon: "folders" },
    { path: "events", label: "Events", icon: "timeline-event" },
    { path: "activity", label: "Activity", icon: "history" },
];

/**
 * Folds to icons with the button at its bottom (remembered), and on its own on narrower windows; a strip across the
 * top on a phone.
 */
export function Sidebar({ session, current, collapsed, onCollapse }: { session?: Session; current: string; collapsed: boolean; onCollapse: (collapsed: boolean) => void }) {
    const running = useOperations().filter((o) => o.state === "running");
    return (
        <aside className="sidebar">
            <nav className="sidebar-nav" aria-label="Pages">
                {pages
                    .filter((p) => (p.path !== "events" || session?.eventStore !== false) && (p.path !== "store" || session?.imageStore !== false))
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
                <button
                    type="button"
                    className="sidebar-link sidebar-collapse"
                    aria-expanded={!collapsed}
                    title={collapsed ? "Expand the sidebar" : "Collapse the sidebar"}
                    onClick={() => onCollapse(!collapsed)}
                >
                    <Icon name={collapsed ? "layout-sidebar-left-expand" : "layout-sidebar-left-collapse"} className="sidebar-icon" />
                    <span className="sidebar-label">Collapse</span>
                </button>
            </div>
        </aside>
    );
}
