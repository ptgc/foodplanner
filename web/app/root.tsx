import { Links, Meta, Outlet, Scripts, ScrollRestoration } from "react-router";
import type { ReactNode } from "react";
import { APP_NAME, retryLoadingMessages, useI18n, useMessagesStatus } from "./i18n";
import { UpdateBanner } from "./pwa/update-banner";
import { SessionProvider } from "./session";
import "./app.css";
import "./recipes/recipes.css";
import "./planner/planner.css";
import "./recipes/recipe-edit.css";
import "./recipes/cook.css";
import "./imports/import.css";
import "./household/household.css";
import "./kid/avatar.css";

export function Layout({ children }: { children: ReactNode }) {
  return (
    // The prerendered page is English; the active language is set on <html lang> as soon as the app loads (i18n/index.ts).
    <html lang="en">
      <head>
        <meta charSet="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <title>{APP_NAME}</title>
        {/* PWA (T-041): installable; the manifest and icons are static files in public/. */}
        <link rel="manifest" href="/manifest.webmanifest" />
        <meta name="theme-color" content="#1f6f43" />
        <link rel="icon" href="/icons/icon-192.png" type="image/png" />
        <link rel="apple-touch-icon" href="/icons/apple-touch-icon.png" />
        <Meta />
        <Links />
      </head>
      <body>
        {children}
        <ScrollRestoration />
        <Scripts />
      </body>
    </html>
  );
}

export function HydrateFallback() {
  const { t } = useI18n();
  return <p role="status">{t("common.loading")}</p>;
}

/**
 * Shown when no language could be loaded, so it cannot use t(): the text is hard-coded in both languages on purpose
 * (the one place besides the tests where text is not translated through the message files).
 */
function MessagesFailed() {
  /* eslint-disable foodplanner/no-literal-text */
  return (
    <div role="alert" style={{ padding: "1rem" }}>
      <p>Kunne ikke laste / Could not load</p>
      <button type="button" className="button" style={{ minHeight: 44, minWidth: 44 }} onClick={retryLoadingMessages}>
        Prøv igjen / Try again
      </button>
    </div>
  );
  /* eslint-enable foodplanner/no-literal-text */
}

export default function App() {
  // Nothing but the loader until the active language's messages are in: no flash of untranslated keys (T-106).
  const status = useMessagesStatus();
  if (status === "failed") return <MessagesFailed />;
  if (status !== "ready") return <HydrateFallback />;
  return (
    <SessionProvider>
      <Outlet />
      <UpdateBanner />
    </SessionProvider>
  );
}
