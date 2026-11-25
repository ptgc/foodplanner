import { Suspense } from "react";
import { useI18n } from "../i18n";
import { lazyWithMessages } from "../i18n/lazy";

// The system admin support pages are opened by one or two people: their code and their messages (the "support" group)
// load only when /support is opened (T-096). "Mer" lists it for a system admin (T-137); the page shows itself only to a system admin.
const SupportPage = lazyWithMessages("support", () => import("../support/support-page"));

export default function SupportRoute() {
  const { t } = useI18n();
  return (
    <Suspense fallback={<p role="status">{t("common.loading")}</p>}>
      <SupportPage />
    </Suspense>
  );
}
