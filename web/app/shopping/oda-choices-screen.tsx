import { useState } from "react";
import { Link } from "react-router";
import { powers } from "../household/roles";
import { useLoaded } from "../household/use-loaded";
import { useI18n, type MessageKey } from "../i18n";
import { clearPreference, listPreferences, useOnline, type OdaPreference } from "./oda-preferences";
import { OdaSyncStart } from "./oda-sync-start";
import { useSession } from "../session";

/**
 * The household's Oda choices (T-052): one row per ingredient with the date it was set and "Clear". Product names are not stored, so
 * none is shown; a choice is set on the Oda result screen, the only time names are available. No Oda connection is made here.
 * Adults and owners only (the API answers 403 to anyone else).
 */
export default function OdaChoices() {
  const { t } = useI18n();
  const { session } = useSession();
  if (session.status !== "signedIn") return null;
  const household = session.households.find((h) => h.id === session.activeHouseholdId);
  if (!household) return <p>{t("shopping.noHousehold")}</p>;
  if (!powers(household.role).oda) {
    return (
      <section className="household oda-choices" aria-labelledby="oda-choices-title">
        <h1 id="oda-choices-title">{t("oda.choices.title")}</h1>
        <p data-testid="oda-choices-adults-only">{t("oda.choices.adultsOnly")}</p>
        <Link to="/shopping">{t("oda.result.back")}</Link>
      </section>
    );
  }

  return <ChoicesScreen key={household.id} householdId={household.id} />;
}

function ChoicesScreen({ householdId }: { householdId: string }) {
  const { t, locale } = useI18n();
  const online = useOnline();
  const [error, setError] = useState<MessageKey | null>(null);
  const [cleared, setCleared] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const { data: rows, error: loadError, setData } = useLoaded<OdaPreference[]>(householdId, () => listPreferences(householdId));

  const clear = async (row: OdaPreference) => {
    setBusy(row.ingredientId);
    setError(null);
    setCleared(false);
    const result = await clearPreference(householdId, row.ingredientId);
    setBusy(null);
    if (result.ok) {
      setData((current) => (current ?? []).filter((r) => r.ingredientId !== row.ingredientId));
      setCleared(true);
    } else setError(result.key);
  };

  const dateText = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(iso));

  return (
    <section className="household oda-choices" aria-labelledby="oda-choices-title" data-testid="oda-choices">
      <h1 id="oda-choices-title">{t("oda.choices.title")}</h1>
      <OdaSyncStart householdId={householdId} online={online} />
      <p>{t("oda.choices.intro")}</p>
      <p>
        <Link to="/shopping">{t("oda.result.back")}</Link>
      </p>
      {!online && (
        <p role="status" className="shop-hint" data-testid="oda-choices-offline">
          {t("oda.choices.offline")}
        </p>
      )}
      {loadError && (
        <p role="alert" className="form-error">
          {t(loadError)}
        </p>
      )}
      {error && (
        <p role="alert" className="form-error" data-testid="oda-choices-error">
          {t(error)}
        </p>
      )}
      {cleared && (
        <p role="status" data-testid="oda-choices-cleared">
          {t("oda.choices.cleared")}
        </p>
      )}
      <div className="household-card">
        {!rows && !loadError && <p role="status">{t("oda.choices.loading")}</p>}
        {rows && rows.length === 0 && <p data-testid="oda-choices-empty">{t("oda.choices.empty")}</p>}
        {rows && rows.length > 0 && (
          <ul className="household-list" data-testid="oda-choices-list">
            {rows.map((row) => (
              <li key={row.ingredientId} className="household-row" data-testid="oda-choice-row">
                <div className="household-row-main">
                  <span className="household-row-title">{row.ingredientName}</span>
                  <span className="household-row-sub">
                    {t("oda.result.choice")}, {t("oda.choices.setOn", { date: dateText(row.setAt) })}
                  </span>
                </div>
                <div className="household-row-actions">
                  <button
                    type="button"
                    className="button button-outline oda-pick-button"
                    aria-label={t("oda.choices.clearFor", { name: row.ingredientName })}
                    disabled={!online || busy !== null}
                    onClick={() => void clear(row)}
                  >
                    {t("oda.choices.clear")}
                  </button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}
