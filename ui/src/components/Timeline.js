import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Fragment } from "react";
import { tr } from "../lib/i18n";
const STEPS = [
    ["understanding", "Understanding"],
    ["analyzing", "Analyzing"],
    ["selecting_tool", "Selecting tool"],
    ["executing", "Executing"],
    ["completed", "Completed"],
];
/**
 * The visible life of a request. It shows *what* JARVIS is doing (phase and tool), never the
 * model's private reasoning.
 */
export function Timeline({ turn }) {
    const failed = turn.phase === "failed";
    const currentIndex = STEPS.findIndex(([p]) => p === turn.phase);
    return (_jsxs("div", { className: "timeline", role: "status", "aria-live": "polite", children: [STEPS.map(([phase, label], i) => {
                const visited = turn.visited.includes(phase);
                const isLast = i === STEPS.length - 1;
                let cls = "";
                if (failed && isLast)
                    cls = "tl-failed";
                else if (turn.phase === phase && !turn.result)
                    cls = "tl-active";
                else if (visited || (currentIndex > i) || (turn.result && phase === "completed"))
                    cls = "tl-done";
                return (_jsxs(Fragment, { children: [_jsxs("span", { className: `tl-step ${cls}`, children: [_jsx("i", {}), tr(failed && isLast ? "Failed" : label)] }), !isLast && _jsx("span", { className: "tl-sep" })] }, phase));
            }), turn.detail && !turn.result && _jsx("span", { className: "tl-detail", children: turn.detail })] }));
}
