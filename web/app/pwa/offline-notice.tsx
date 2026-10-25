import { Link } from "react-router";
import { useI18n } from "../i18n";

/** True when the browser knows it has no connection. Only used to choose a friendlier message than "could not reach". */
export function isOffline(): boolean {
  return typeof navigator !== "undefined" && navigator.onLine === false;
}

/** Shown instead of a load error when offline: this page's data was not saved on this device. */
export function OfflineNotice({ showLink = true }: { showLink?: boolean }) {
  const { t } = useI18n();
  return (
    <div className="recipes-message" role="alert" data-testid="offline-notice">
      <h2>{t("pwa.offlineTitle")}</h2>
      <p>{t("pwa.offlineBody")}</p>
      {showLink && (
        <Link to="/recipes" className="button">
          {t("pwa.offlineRecipes")}
        </Link>
      )}
    </div>
  );
}
