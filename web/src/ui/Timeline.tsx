import type { ReactNode } from "react";
import type { Status } from "../lib/types";
import { Pill } from "./Pill";
import "./Timeline.css";

/** One line of a timeline: when, what (and about what), and how it ended or how healthy it is. */
export function TimelineRow({
    time,
    title,
    sub,
    detail,
    detailTone,
    status,
}: {
    time?: ReactNode;
    title: ReactNode;
    sub?: ReactNode;
    detail?: ReactNode;
    detailTone?: "danger" | "warning";
    status?: Status;
}) {
    return (
        <div className="timeline-row">
            {time !== undefined && <span className="timeline-time">{time}</span>}
            <span className="timeline-main">
                <span className="timeline-title">{title}</span>
                {sub && <span className="timeline-sub">{sub}</span>}
                {detail && <span className={`timeline-detail ${detailTone ? `text-${detailTone}` : ""}`}>{detail}</span>}
            </span>
            {status && <Pill status={status} />}
        </div>
    );
}

/** "Today", "Yesterday", "18 Nov" above a day's rows. */
export function DayHeading({ children }: { children: ReactNode }) {
    return <h3 className="timeline-day">{children}</h3>;
}
