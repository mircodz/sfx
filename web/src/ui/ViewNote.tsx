import { api } from "../lib/api";
import { plural } from "../lib/format";
import { useResource } from "../lib/resource";
import { chooseView, useView } from "../lib/view";
import { Button } from "./Button";
import { Icon } from "./Icon";
import "./ViewNote.css";

/**
 * Under a list's header while a view is on: that the list is narrowed, by how much, and whether something unhealthy
 * is out of sight. A view is a lens, so what it hides is never silent.
 */
export function ViewNote() {
    const view = useView();
    const overview = useResource("overview", api.overview);
    if (!view) return null;
    const v = overview.data?.view;
    return (
        <div className="view-note" role="status">
            <Icon name="filter" className="view-note-icon" />
            <span className="view-note-text">
                Through view <strong>{view}</strong>
                {v && `: ${v.applications.toLocaleString()} of ${(v.applications + v.hiddenApplications).toLocaleString()} applications`}.
                {v && v.hiddenUnhealthy > 0 && <span className="text-warning"> {plural(v.hiddenUnhealthy, "unhealthy application")} outside it.</span>}
            </span>
            <Button onClick={() => chooseView(null)}>Show all</Button>
        </div>
    );
}
