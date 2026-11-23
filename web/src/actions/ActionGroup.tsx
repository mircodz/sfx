import { useState } from "react";
import type { Action, ApplicationDetail } from "../lib/types";
import { Button } from "../ui/Button";
import { Menu } from "../ui/Menu";
import { ConfirmAction } from "./ConfirmAction";
import { ApplicationEditor } from "./editors/ApplicationEditor";
import { GenericEditor } from "./editors/GenericEditor";
import { ScaleEditor } from "./editors/ScaleEditor";
import { ClusterUpgradeEditor } from "./editors/ClusterUpgradeEditor";
import { ServiceSettingsEditor } from "./editors/ServiceSettingsEditor";

// The id the server gives the "edit settings" action (dotted like the others, or as first agreed).
const settingsActions = new Set(["service.settings", "service.edit-settings", "edit-service-settings"]);
import "./actions.css";

/** What some editors need from the page they're opened on. */
export type ActionContext = { application?: ApplicationDetail; service?: { appId: string; serviceId: string } };

/**
 * An entity's actions: the two most common as buttons, the rest (and everything destructive) in a "More actions"
 * menu. Actions with a form open the side panel; the others confirm in a dialog.
 */
export function ActionGroup({ actions, context, label = "Actions" }: { actions: Action[]; context?: ActionContext; label?: string }) {
    const [open, setOpen] = useState<Action | null>(null);
    if (actions.length === 0) return null;
    const safe = actions.filter((a) => a.risk !== "destructive");
    const shown = safe.slice(0, 2);
    const more = [...safe.slice(2), ...actions.filter((a) => a.risk === "destructive")];
    const close = () => setOpen(null);
    return (
        <div className="action-group" role="group" aria-label={label}>
            {shown.map((a) => (
                <Button key={a.id} onClick={() => setOpen(a)}>
                    {a.title}
                </Button>
            ))}
            {more.length > 0 && <Menu label="More actions" items={more.map((a) => ({ id: a.id, label: a.title, danger: a.risk === "destructive", onPick: () => setOpen(a) }))} />}
            {open && <ActionView action={open} context={context} onClose={close} />}
        </div>
    );
}

function ActionView({ action, context, onClose }: { action: Action; context?: ActionContext; onClose: () => void }) {
    if (!action.form) return <ConfirmAction action={action} onClose={onClose} />;
    if (action.id === "application.upgrade" && context?.application) return <ApplicationEditor action={action} app={context.application} onClose={onClose} />;
    if (action.id === "service.scale") return <ScaleEditor action={action} onClose={onClose} />;
    if (action.id === "cluster.upgrade") return <ClusterUpgradeEditor action={action} onClose={onClose} />;
    if (settingsActions.has(action.id) && context?.service) return <ServiceSettingsEditor action={action} {...context.service} onClose={onClose} />;
    return <GenericEditor action={action} onClose={onClose} />;
}
