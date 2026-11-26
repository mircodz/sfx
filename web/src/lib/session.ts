import { createContext, useContext, useEffect, useRef } from "react";
import { onSwitch, show, switchedHere } from "./connection";
import type { Resource } from "./resource";
import type { Session } from "./types";

/** The connected cluster's session, for views that adapt to it (e.g. no events without an event store). */
export const SessionContext = createContext<Session | undefined>(undefined);

export const useSession = () => useContext(SessionContext);

/** True until the session says otherwise, so nothing flickers away on load. */
export const useEventsAvailable = () => useSession()?.eventStore ?? true;

export const useRepairsAvailable = () => useSession()?.repairManager ?? true;

/**
 * Follows a switch made in another tab: once an answer says sfx ui is on another connection, loads its session, reloads
 * everything and says so, opening the overview if it's another cluster.
 */
export function useFollowSwitches(session: Resource<Session>, bump: () => void, notify: (text: string, ok: boolean) => void) {
    const reload = session.reload;
    useEffect(() => onSwitch(reload), [reload]);

    const shown = useRef<Session | undefined>(undefined);
    useEffect(() => {
        const next = session.data;
        if (!next) return;
        const before = shown.current;
        shown.current = next;
        show(next.id);
        if (!before || before.id === next.id) return;
        bump();
        if (switchedHere(next.context)) return;
        if (before.context === next.context) {
            notify(`Reconnected to ${next.context} from another tab.`, true);
        } else {
            notify(`Switched to ${next.context} from another tab.`, true);
            location.hash = "#/";
        }
    }, [session.data, bump, notify]);
}
