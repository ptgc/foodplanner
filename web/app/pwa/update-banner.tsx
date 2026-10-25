import { useI18n } from "../i18n";
import { useServiceWorkerUpdate, type ServiceWorkerOptions } from "./use-service-worker";

/** "New version available - Reload", shown when a new service worker is waiting. Renders nothing otherwise. */
export function UpdateBanner(options: ServiceWorkerOptions) {
  const { t } = useI18n();
  const { updateAvailable, applyUpdate } = useServiceWorkerUpdate(options);
  if (!updateAvailable) return null;
  return (
    <div className="update-banner" role="status" data-testid="update-banner">
      <span>{t("pwa.updateAvailable")}</span>
      <button type="button" className="button button-light" onClick={applyUpdate}>
        {t("pwa.reload")}
      </button>
    </div>
  );
}
