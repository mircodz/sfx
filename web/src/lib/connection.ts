/** Which sfx ui connection (session id) this tab shows, and whether the server has since moved to another. */

const listeners = new Set<() => void>();
let shown = "";
let announced = "";
let requested: string | undefined;

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

/** This tab asked to switch to `name`; its contexts page says so itself. */
export function requestSwitch(name: string) {
    requested = name;
}

/** Whether this tab asked for the switch to `name`; true once. */
export function switchedHere(name: string) {
    const here = requested === name;
    requested = undefined;
    return here;
}
