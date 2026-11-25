import { Suspense, lazy, useEffect, useRef, useState } from "react";
import { Link, NavLink, Navigate, Outlet, useLocation } from "react-router";
import { loginPathFor } from "../auth-paths";
import { APP_NAME, useI18n, type MessageKey } from "../i18n";
import { lazyWithMessages } from "../i18n/lazy";
import { useSession } from "../session";

// Kid mode and the profile picker are rarely used on a grown-up session: their own chunks keep the first load small.
const KidApp = lazyWithMessages("kid", () => import("../kid/kid-app"));
const ProfilePicker = lazyWithMessages("kid", () => import("../kid/profile-picker").then((m) => ({ default: m.ProfilePicker })));

// The profile sheet is opened now and then: its code (and the dialog and language parts) load on the first tap.
const ProfileSheet = lazy(() => import("./profile-sheet").then((m) => ({ default: m.ProfileSheet })));

// The first path segment to the top bar title, and true when the screen lives under the "More" tab.
const sections: Record<string, [MessageKey, boolean]> = {
  "": ["nav.today", false],
  plan: ["nav.plan", false],
  recipes: ["nav.recipes", false],
  shopping: ["nav.shopping", false],
  oda: ["nav.shopping", false],
  more: ["nav.more", true],
  pantry: ["pantry.title", true],
  "what-can-i-make": ["nav.suggest", true],
  household: ["nav.household", true],
  ingredients: ["recipes.ingredients", true],
  support: ["nav.support", true],
  status: ["nav.support", true],
};

type Section = { title: MessageKey; more: boolean };

/** Which screen a path belongs to: the top bar title, and whether the "Mer" tab is the active one (T-137). */
function sectionOf(pathname: string): Section | undefined {
  const [title, more] = sections[pathname.split("/")[1] ?? ""] ?? [];
  return title ? { title, more: more === true } : undefined;
}

export default function Shell() {
  const { t } = useI18n();
  const { session } = useSession();
  const location = useLocation();
  const mainRef = useRef<HTMLElement>(null);
  const signedIn = session.status === "signedIn";
  const [picking, setPicking] = useState(false);
  const [profileOpen, setProfileOpen] = useState(false);

  // Move focus to the content when the route changes, so keyboard and screen reader users start there.
  useEffect(() => {
    if (signedIn) mainRef.current?.focus({ preventScroll: true });
  }, [location.pathname, signedIn]);

  if (session.status === "loading") return <p role="status">{t("common.loading")}</p>;
  if (session.status === "signedOut") {
    // Just deleted the account (T-095): the sign-in page says so, and there is nothing to come back to.
    const target = session.accountDeleted ? "/login?accountDeleted=1" : loginPathFor(location);
    return <Navigate to={target} replace />;
  }

  // The server says this device is a child profile (T-026): show the kid shell, nothing of the grown-up app.
  if (session.user.kidMode) {
    return (
      <Suspense fallback={<p role="status">{t("common.loading")}</p>}>
        <KidApp />
      </Suspense>
    );
  }

  const active = session.households.find((h) => h.id === session.activeHouseholdId);
  const canSwitchProfile = active !== undefined && active.role !== "Child";
  const section = sectionOf(location.pathname);

  return (
    <div className="shell">
      <a className="skip-link" href="#main">
        {t("common.skipToContent")}
      </a>
      <header className="shell-header">
        <span className="shell-title">{section ? t(section.title) : APP_NAME}</span>
        <button
          type="button"
          className="shell-profile"
          data-testid="profile-button"
          aria-label={t("shell.profile")}
          onClick={() => setProfileOpen(true)}
        >
          {session.user.email.charAt(0).toUpperCase()}
        </button>
      </header>
      <main id="main" ref={mainRef} tabIndex={-1} className="shell-main">
        <Outlet />
      </main>
      <nav className="shell-tabs" aria-label={t("shell.mainNavigation")}>
        <NavLink to="/" end>
          {t("nav.today")}
        </NavLink>
        <NavLink to="/plan">{t("nav.plan")}</NavLink>
        <NavLink to="/recipes">{t("nav.recipes")}</NavLink>
        <NavLink to="/shopping">{t("nav.shopping")}</NavLink>
        <Link to="/more" aria-current={section?.more ? "page" : undefined}>
          {t("nav.more")}
        </Link>
      </nav>
      {profileOpen && (
        <Suspense fallback={null}>
          <ProfileSheet
            canSwitchProfile={canSwitchProfile}
            onClose={() => setProfileOpen(false)}
            onSwitchProfile={() => {
              setProfileOpen(false);
              setPicking(true);
            }}
          />
        </Suspense>
      )}
      {picking && active && (
        <Suspense fallback={null}>
          <ProfilePicker householdId={active.id} kidMode={false} activeProfileId={null} onClose={() => setPicking(false)} />
        </Suspense>
      )}
    </div>
  );
}
