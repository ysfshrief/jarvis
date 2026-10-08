import { useEffect, useState } from "react";
import { get } from "../api";
/**
 * Polls machine health while the page is visible. The runtime only samples when asked, so a
 * hidden or closed dashboard costs nothing.
 */
export function useMetrics(intervalMs = 3000) {
    const [data, setData] = useState(null);
    const [error, setError] = useState(null);
    useEffect(() => {
        let stopped = false;
        let timer = 0;
        const tick = async () => {
            if (document.visibilityState === "visible") {
                try {
                    const m = await get("/system/metrics");
                    if (!stopped) {
                        setData(m);
                        setError(null);
                    }
                }
                catch (e) {
                    if (!stopped)
                        setError(e);
                }
            }
            if (!stopped)
                timer = window.setTimeout(tick, intervalMs);
        };
        void tick();
        const onVis = () => { if (document.visibilityState === "visible") {
            window.clearTimeout(timer);
            void tick();
        } };
        document.addEventListener("visibilitychange", onVis);
        return () => { stopped = true; window.clearTimeout(timer); document.removeEventListener("visibilitychange", onVis); };
    }, [intervalMs]);
    return { data, error };
}
