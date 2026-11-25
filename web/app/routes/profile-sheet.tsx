import { useId, useRef } from "react";
import { useI18n } from "../i18n";
import { LanguageSwitch } from "../i18n/language-switch";
import { useSession } from "../session";
import { useModal } from "../use-modal";

/** The profile sheet (T-137): who is signed in, which household, language, switch profile, log out. */
export function ProfileSheet({ canSwitchProfile, onSwitchProfile, onClose }: { canSwitchProfile: boolean; onSwitchProfile: () => void; onClose: () => void }) {
  const { t } = useI18n();
  const { session, signOut, setActiveHousehold } = useSession();
  const ref = useRef<HTMLDivElement>(null);
  useModal(ref, onClose);
  const headingId = useId();
  if (session.status !== "signedIn") return null;
  const active = session.households.find((h) => h.id === session.activeHouseholdId);

  return (
    <div className="profile-backdrop" role="presentation" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div ref={ref} className="profile-sheet" data-testid="profile-sheet" role="dialog" aria-modal="true" aria-labelledby={headingId} tabIndex={-1}>
        <div className="profile-head">
          <h2 id={headingId}>{t("shell.profile")}</h2>
          <button type="button" className="button button-outline" onClick={onClose}>
            {t("planner.pickerClose")}
          </button>
        </div>
        <p className="shell-email" data-testid="user-email">
          {session.user.email}
        </p>
        <div className="profile-line">
          {session.households.length > 1 ? (
            <>
              <label htmlFor="household" className="profile-label">
                {t("shell.household")}
              </label>
              <select id="household" value={session.activeHouseholdId ?? ""} onChange={(e) => setActiveHousehold(e.target.value)}>
                {session.households.map((h) => (
                  <option key={h.id} value={h.id}>
                    {h.name}
                  </option>
                ))}
              </select>
            </>
          ) : (
            <>
              <span className="profile-label">{t("shell.household")}</span>
              <span data-testid="household-name">{active?.name ?? t("shell.noHousehold")}</span>
            </>
          )}
        </div>
        <div className="profile-line">
          <span className="profile-label" aria-hidden="true">
            {t("common.language")}
          </span>
          <LanguageSwitch />
        </div>
        {canSwitchProfile && (
          <button type="button" className="button button-outline" data-testid="switch-profile" onClick={onSwitchProfile}>
            {t("kid.switchProfile")}
          </button>
        )}
        <button type="button" className="button" onClick={() => void signOut()}>
          {t("shell.logOut")}
        </button>
      </div>
    </div>
  );
}
