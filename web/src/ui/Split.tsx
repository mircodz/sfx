import type { ReactNode } from "react";
import "./Split.css";

/**
 * A detail page's Overview on a wide screen: what it contains (children) on the left, what it is (errors and
 * warnings, facts, load) in a narrow column on the right. Narrower, the aside comes first, as one column.
 */
export function Split({ aside, children }: { aside: ReactNode; children: ReactNode }) {
    return (
        <div className="split-wrap">
            <div className="split">
                <aside className="split-aside">{aside}</aside>
                <div className="split-main">{children}</div>
            </div>
        </div>
    );
}
