import { ActionGroup } from "../actions/ActionGroup";
import { ago } from "../lib/format";
import type { Upgrade } from "../lib/types";
import { modeText, upgradeProgress } from "../lib/upgrades";
import { DomainStrip } from "../ui/DomainStrip";
import { ErrorsAndWarnings } from "../ui/Health";
import { ProgressBar } from "../ui/Meter";

const phases: Record<string, string> = {
    WaitDuration: "Waiting before the health check",
    StableDuration: "Checking that health holds",
    Retry: "Retrying an unhealthy check",
};

/**
 * One upgrade, in sections divided by hairlines: progress and domains, what holds it up, then a footer with where it
 * stands on the left and its controls on the right, like every other card's actions.
 */
export function UpgradeCard({ upgrade: u, actions = true }: { upgrade: Upgrade; actions?: boolean }) {
    const p = upgradeProgress(u);
    const phase = phases[u.healthCheckPhase];
    const controls = actions && u.actions.length > 0;
    return (
        <div className="upgrade-card">
            <div>
                <ProgressBar pct={p.pct} label={`${u.name} upgrade progress, ${p.done} of ${p.total} domains`} />
                <DomainStrip upgrade={u} />
            </div>
            {u.failureReason && u.failureReason !== "None" && <p className="text-danger small">Failed: {u.failureReason}.</p>}
            {u.evaluations.length > 0 && <ErrorsAndWarnings evaluations={u.evaluations} />}
            <div className="upgrade-card-footer">
                <span className="secondary small">
                    {modeText(u.mode)}
                    {u.started ? ` · started ${ago(u.started)}` : ""}
                    {phase ? ` · ${phase}` : ""}
                </span>
                {controls && <ActionGroup actions={u.actions} label={`Controls for the upgrade of ${u.name}`} />}
            </div>
        </div>
    );
}
