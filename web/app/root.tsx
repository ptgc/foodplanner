import { Links, Meta, Outlet, Scripts, ScrollRestoration } from "react-router";
import type { ReactNode } from "react";
import { APP_NAME, useI18n } from "./i18n";
import { SessionProvider } from "./session";
import "./app.css";
import "./recipes/recipes.css";
import "./planner/planner.css";

export function Layout({ children }: { children: ReactNode }) {
  return (
    // The prerendered page is English; the active language is set on <html lang> as soon as the app loads (i18n/index.ts).
    <html lang="en">
      <head>
        <meta charSet="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <title>{APP_NAME}</title>
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
    </SessionProvider>
  );
}
