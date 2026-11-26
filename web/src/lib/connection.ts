/** Which sfx ui connection (session id) this tab shows, and whether the server has since moved to another. */

const listeners = new Set<() => void>();
let shown = "";
let announced = "";

/** Sent with every request; the server refuses changes unless it's still its connection. */
export const shownConnection = () => shown;

export function show(id: string) {
    shown = id;
    announced = id;
}

/** With each answer's connection: tells the listeners, once per connection, that it isn't the one shown. */
export function observe(current: string | null) {
    if (!shown || !current || current === shown || current === announced) return;
    announced = current;
    for (const listener of listeners) listener();
}

export function onSwitch(listener: () => void) {
    listeners.add(listener);
    return () => {
        listeners.delete(listener);
    };
}

// What this tab's contexts page is doing, so a switch it announces itself isn't announced twice, and one it gave up on
// (failed, dismissed, or left) is.
let awaited: string | undefined;
let asked: string | undefined;
let announcedHere: string | undefined;

/** The contexts page asked to switch to `name` and waits for it. */
export function switchStarted(name: string) {
    awaited = name;
    asked = name;
}

/** It failed: refused, or the connection's error is showing. */
export function switchFailed() {
    awaited = undefined;
    asked = undefined;
}

/** The contexts page stopped waiting (dismissed, or left); the switch may still complete. */
export function switchAbandoned() {
    awaited = undefined;
}

/** The contexts page said the switch to `connection` happened, and opened the overview. */
export function switchAnnounced(connection: string) {
    announcedHere = connection;
    awaited = undefined;
    asked = undefined;
}

export type Followed = { notice: string; overview: boolean };

/** What a tab does once it shows `next` instead of `before`: nothing when its contexts page says so itself. */
export function follow(before: { id: string; context: string }, next: { id: string; context: string }): Followed | undefined {
    if (next.id === announcedHere || next.context === awaited) return undefined;
    const mine = asked === next.context;
    asked = undefined;
    if (before.context === next.context) return { notice: `Reconnected to ${next.context}${mine ? "" : " from another tab"}.`, overview: false };
    return { notice: `Switched to ${next.context}${mine ? "" : " from another tab"}.`, overview: true };
}
