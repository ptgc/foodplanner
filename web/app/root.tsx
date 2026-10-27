import { Links, Meta, Outlet, Scripts, ScrollRestoration } from "react-router";
import type { ReactNode } from "react";
import { APP_NAME, useI18n } from "./i18n";
import { UpdateBanner } from "./pwa/update-banner";
import { SessionProvider } from "./session";
import "./app.css";
import "./recipes/recipes.css";
import "./planner/planner.css";
import "./recipes/recipe-edit.css";
import "./recipes/cook.css";
import "./imports/import.css";
import "./household/household.css";

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

export default function App() {
  return (
    <SessionProvider>
      <Outlet />
      <UpdateBanner />
    </SessionProvider>
  );
}
