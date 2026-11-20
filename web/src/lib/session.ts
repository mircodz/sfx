import { createContext, useContext } from "react";
import type { Session } from "./types";

/** The connected cluster's session, for views that adapt to it (e.g. no events without an event store). */
export const SessionContext = createContext<Session | undefined>(undefined);

export const useSession = () => useContext(SessionContext);

/** Whether to offer events; true until the session says otherwise, so nothing flickers away on load. */
export const useEventsAvailable = () => useSession()?.eventStore ?? true;

/** Whether to offer repair tasks; true until the session says otherwise. */
export const useRepairsAvailable = () => useSession()?.repairManager ?? true;
