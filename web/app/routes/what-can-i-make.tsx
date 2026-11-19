import { Suspense } from "react";
import { useI18n } from "../i18n";
import { lazyWithMessages } from "../i18n/lazy";

// The screen is opened now and then: its code, its styles and its messages (the "suggest" group) load only when it is.
const WhatCanIMake = lazyWithMessages("suggest", () => import("../suggest/what-can-i-make"));

export default function WhatCanIMakeRoute() {
  const { t } = useI18n();
  return (
    <Suspense fallback={<p role="status">{t("common.loading")}</p>}>
      <WhatCanIMake />
    </Suspense>
  );
}
