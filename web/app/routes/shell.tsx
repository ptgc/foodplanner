import { useEffect, useRef } from "react";
import { NavLink, Navigate, Outlet, useLocation } from "react-router";
import { loginPathFor } from "../auth-paths";
import { APP_NAME, useI18n } from "../i18n";
import { LanguageSwitch } from "../i18n/language-switch";
import { useSession } from "../session";

export default function Shell() {
  const { t } = useI18n();
  const { session, signOut, setActiveHousehold } = useSession();
  const location = useLocation();
  const mainRef = useRef<HTMLElement>(null);
  const signedIn = session.status === "signedIn";

  // Move focus to the content when the route changes, so keyboard and screen reader users start there.
  useEffect(() => {
    if (signedIn) mainRef.current?.focus({ preventScroll: true });
  }, [location.pathname, signedIn]);

  if (session.status === "loading") return <p role="status">{t("common.loading")}</p>;
  if (session.status === "signedOut") return <Navigate to={loginPathFor(location)} replace />;

  const active = session.households.find((h) => h.id === session.activeHouseholdId);

  return (
    <div className="shell">
      <a className="skip-link" href="#main">
        {t("common.skipToContent")}
      </a>
      <header className="shell-header">
        <span className="shell-title">{APP_NAME}</span>
        <div className="shell-session">
          <span className="shell-email" data-testid="user-email">
            {session.user.email}
          </span>
          {session.households.length > 1 ? (
            <>
              <label htmlFor="household" className="visually-hidden">
                {t("shell.household")}
              </label>
              <select
                id="household"
                value={session.activeHouseholdId ?? ""}
                onChange={(e) => setActiveHousehold(e.target.value)}
              >
                {session.households.map((h) => (
                  <option key={h.id} value={h.id}>
                    {h.name}
                  </option>
                ))}
              </select>
            </>
          ) : (
            <span data-testid="household-name">{active?.name ?? t("shell.noHousehold")}</span>
          )}
          <LanguageSwitch />
          <button type="button" className="button button-light" onClick={() => void signOut()}>
            {t("shell.logOut")}
          </button>
        </div>
      </header>
      <nav className="shell-nav" aria-label={t("shell.mainNavigation")}>
        <NavLink to="/" end>
          {t("nav.home")}
        </NavLink>
        <NavLink to="/recipes">{t("nav.recipes")}</NavLink>
        <NavLink to="/plan">{t("nav.plan")}</NavLink>
        <NavLink to="/shopping">{t("nav.shopping")}</NavLink>
        <NavLink to="/household">{t("nav.household")}</NavLink>
      </nav>
      <main id="main" ref={mainRef} tabIndex={-1} className="shell-main">
        <Outlet />
      </main>
    </div>
  );
}
