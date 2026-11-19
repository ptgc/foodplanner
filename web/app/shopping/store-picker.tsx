import { useEffect, useId, useState } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { useI18n } from "../i18n";
import type { ActionResult } from "./use-shopping-list";

type Store = components["schemas"]["StoreResponse"];

/**
 * Which store the user is shopping at (T-087). Hidden while the household has no stores. The choice is the user's own and is
 * remembered on the server, so it follows them across devices; the list below is re-read in the store's section order.
 */
export function StorePicker({
  householdId,
  online,
  onChoose,
}: {
  householdId: string;
  online: boolean;
  onChoose: (storeId: string | null) => Promise<ActionResult>;
}) {
  const { t } = useI18n();
  const id = useId();
  const [stores, setStores] = useState<Store[]>([]);
  const [chosen, setChosen] = useState<string>("");
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const [list, mine] = await Promise.all([
          api.GET("/api/households/{householdId}/stores", { params: { path: { householdId } } }),
          api.GET("/api/households/{householdId}/chosen-store", { params: { path: { householdId } } }),
        ]);
        if (cancelled) return;
        if (list.data) setStores(list.data);
        if (mine.data) setChosen(mine.data.storeId ?? "");
      } catch {
        // Offline: the picker keeps what it has (or stays hidden); the cached list keeps its last order.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId]);

  if (stores.length === 0) return null;

  const choose = async (value: string) => {
    const before = chosen;
    setChosen(value);
    setFailed(false);
    const result = await onChoose(value === "" ? null : value);
    if (result !== "ok") {
      setChosen(before);
      setFailed(true);
    }
  };

  return (
    <div className="field shop-store" data-testid="store-picker">
      <label htmlFor={id}>{t("shopping.store")}</label>
      <select id={id} value={chosen} disabled={!online} onChange={(e) => void choose(e.target.value)}>
        <option value="">{t("shopping.storeNone")}</option>
        {stores.map((s) => (
          <option key={s.id} value={s.id}>
            {s.name}
          </option>
        ))}
      </select>
      {failed && (
        <p role="alert" className="form-error">
          {t("shopping.storeFailed")}
        </p>
      )}
    </div>
  );
}
