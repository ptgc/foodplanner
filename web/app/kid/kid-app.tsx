import { useState } from "react";
import { NavLink, Navigate, Outlet, useLocation } from "react-router";
import { useI18n } from "../i18n";
import { useSession } from "../session";
import { ProfileAvatar } from "./avatar";
import { CalendarIcon, CartIcon, DishIcon, KeyIcon } from "./icons";
import { KidHome } from "./kid-home";
import { useProfiles } from "./kid-api";
import { KidWeek } from "./kid-week";
import { PinSheet } from "./pin-sheet";
import { ProfilePicker } from "./profile-picker";

/**
 * The shell of kid mode (T-091), shown instead of the grown-up shell while `/api/auth/me` says `kidMode`. Only an avatar (opens
 * the profile picker) and a key (opens the PIN sheet) are in the header; three big icons below reach the dishes, the week and the
 * shopping list. No menu, no settings, no email. Every other address goes back to the dishes; the server closes the adult API anyway.
 */
export default function KidApp() {
  const { t } = useI18n();
  const { session } = useSession();
  const location = useLocation();
  const kidMode = session.status === "signedIn" ? session.user.kidMode : null;
  const householdId = kidMode?.householdId ?? (session.status === "signedIn" ? session.activeHouseholdId : null);
  const profileId = kidMode?.profileId ?? null;
  const { profiles } = useProfiles(householdId ?? "");
  const profile = profiles?.find((p) => p.id === profileId) ?? null;
  const [opened, setOpened] = useState<"picker" | "pin" | null>(null);
  const [dismissed, setDismissed] = useState(false);

  // A device that is in kid mode with no (or a deleted) profile asks "who are you?" at once.
  const needsPicker = profileId === null || (profiles !== null && profile === null);
  const overlay = opened ?? (needsPicker && !dismissed ? "picker" : null);
  const closeOverlay = () => {
    setOpened(null);
    setDismissed(true);
  };

  if (!householdId) return <p role="status">{t("kid.noHousehold")}</p>;

  const path = location.pathname;
  const screen =
    path === "/" ? (
      <KidHome householdId={householdId} />
    ) : path === "/plan" ? (
      <KidWeek householdId={householdId} profileId={profileId} />
    ) : path === "/shopping" ? (
      <Outlet />
    ) : (
      <Navigate to="/" replace />
    );

  return (
    <div className="kid-shell" data-testid="kid-shell">
      <header className="kid-header">
        <button
          type="button"
          className="kid-avatar-button"
          aria-label={profile ? t("kid.switchProfileNamed", { name: profile.displayName }) : t("kid.switchProfile")}
          data-testid="kid-switch"
          onClick={() => setOpened("picker")}
        >
          {profile ? <ProfileAvatar profile={profile} size="md" /> : <span className="kid-avatar kid-avatar-md" aria-hidden="true">?</span>}
        </button>
        <button type="button" className="kid-key-button" aria-label={t("kid.unlock")} data-testid="kid-unlock" onClick={() => setOpened("pin")}>
          <KeyIcon />
        </button>
      </header>
      <main id="main" className="kid-main" tabIndex={-1}>
        {screen}
      </main>
      <nav className="kid-nav" aria-label={t("kid.nav.label")}>
        <NavLink to="/" end aria-label={t("kid.nav.home")}>
          <DishIcon />
        </NavLink>
        <NavLink to="/plan" aria-label={t("kid.nav.week")}>
          <CalendarIcon />
        </NavLink>
        <NavLink to="/shopping" aria-label={t("kid.nav.shopping")}>
          <CartIcon />
        </NavLink>
      </nav>
      {overlay === "picker" && (
        <ProfilePicker householdId={householdId} kidMode activeProfileId={profileId} onClose={closeOverlay} />
      )}
      {overlay === "pin" && <PinSheet onClose={closeOverlay} />}
    </div>
  );
}
