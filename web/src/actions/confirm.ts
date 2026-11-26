import type { Action } from "../lib/types";

/** Destructive actions with a confirmation text run only once it's typed exactly. */
export function canConfirm(action: Pick<Action, "risk" | "confirmText">, typed: string) {
    return action.risk !== "destructive" || !action.confirmText || typed === action.confirmText;
}
