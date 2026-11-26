import { toneOf } from "../lib/health";
import type { Status } from "../lib/types";
import "./Pill.css";

export function Pill({ status }: { status: Status }) {
    return <span className={`pill tone-${toneOf(status)}`}>{status}</span>;
}
