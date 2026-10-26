import { useCallback, useEffect, useRef, useState, type Dispatch, type SetStateAction } from "react";
import type { MessageKey } from "../i18n";
import type { Outcome } from "./call";

/**
 * Server state for one list: loads when `key` changes, `reload()` loads again, `setData` applies a local edit (for
 * example a changed role) without waiting for the next load. A failed load keeps the previous data and sets `error`.
 */
export function useLoaded<T>(key: string, run: () => Promise<Outcome<T | undefined>>) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<MessageKey | null>(null);
  const [tick, setTick] = useState(0);
  const latest = useRef(run);
  useEffect(() => {
    latest.current = run;
  });

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const result = await latest.current();
      if (cancelled) return;
      if (result.ok) {
        setData(result.data ?? null);
        setError(null);
      } else setError(result.key);
    })();
    return () => {
      cancelled = true;
    };
  }, [key, tick]);

  const reload = useCallback(() => setTick((n) => n + 1), []);
  return { data, error, reload, setData: setData as Dispatch<SetStateAction<T | null>> };
}
