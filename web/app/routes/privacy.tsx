import { Suspense } from "react";
import { useI18n } from "../i18n";
import { lazyWithMessages } from "../i18n/lazy";

// The privacy page is rarely opened: its code and its messages (the "privacy" group) load only when it is.
// It sits outside the shell so it can be read signed out too (linked from the sign-in screen).
const PrivacyPage = lazyWithMessages("privacy", () => import("../privacy/privacy-page"));

export default function PrivacyRoute() {
  const { t } = useI18n();
  return (
    <Suspense fallback={<p role="status">{t("common.loading")}</p>}>
      <PrivacyPage />
    </Suspense>
  );
}
