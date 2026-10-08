import { Fragment } from "react";
import type { LiveTurn, Phase } from "../lib/turns";
import { tr } from "../lib/i18n";

const STEPS: [Phase, string][] = [
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
export function Timeline({ turn }: { turn: LiveTurn }) {
  const failed = turn.phase === "failed";
  const currentIndex = STEPS.findIndex(([p]) => p === turn.phase);
  return (
    <div className="timeline" role="status" aria-live="polite">
      {STEPS.map(([phase, label], i) => {
        const visited = turn.visited.includes(phase);
        const isLast = i === STEPS.length - 1;
        let cls = "";
        if (failed && isLast) cls = "tl-failed";
        else if (turn.phase === phase && !turn.result) cls = "tl-active";
        else if (visited || (currentIndex > i) || (turn.result && phase === "completed")) cls = "tl-done";
        return (
          <Fragment key={phase}>
            <span className={`tl-step ${cls}`}>
              <i />
              {tr(failed && isLast ? "Failed" : label)}
            </span>
            {!isLast && <span className="tl-sep" />}
          </Fragment>
        );
      })}
      {turn.detail && !turn.result && <span className="tl-detail">{turn.detail}</span>}
    </div>
  );
}
