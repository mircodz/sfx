import { toneOf } from "../lib/health";
import type { Status } from "../lib/types";
import "./Pill.css";

/** A status as a word on its status color: "Healthy", "Error", "Upgrading"… */
export function Pill({ status }: { status: Status }) {
    return <span className={`pill tone-${toneOf(status)}`}>{status}</span>;
}
