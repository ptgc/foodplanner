import { Suspense } from "react";
import { lazyWithMessages } from "../i18n/lazy";

// The screen and its texts (message group "oda") arrive together; see shopping/oda-choices-screen.tsx.
const Screen = lazyWithMessages("oda", () => import("../shopping/oda-choices-screen"));

/** The household's Oda choices (T-052), at /shopping/oda-choices. */
export default function OdaChoices() {
  return (
    <Suspense fallback={null}>
      <Screen />
    </Suspense>
  );
}
