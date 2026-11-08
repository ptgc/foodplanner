import { Suspense } from "react";
import { useI18n } from "../i18n";
import { lazyWithMessages } from "../i18n/lazy";

// The delete-account screen is rarely opened: its code and its messages (the "account" group) load only when it is.
const DeleteAccount = lazyWithMessages("account", () => import("../account/delete-account"));

export default function AccountDeleteRoute() {
  const { t } = useI18n();
  return (
    <Suspense fallback={<p role="status">{t("common.loading")}</p>}>
      <DeleteAccount />
    </Suspense>
  );
}
