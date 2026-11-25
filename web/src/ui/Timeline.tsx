import type { ReactNode } from "react";
import type { Status } from "../lib/types";
import { Pill } from "./Pill";
import "./Timeline.css";

/** `sub` is what it's about; `status` is how it ended or how healthy it is. */
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

export function DayHeading({ children }: { children: ReactNode }) {
    return <h3 className="timeline-day">{children}</h3>;
}
