import { useId, useRef, useState } from "react";
import "./kid.css";
import { Link } from "react-router";
import { useI18n, type MessageKey } from "../i18n";
import { useSession } from "../session";
import { ProfileAvatar } from "./avatar";
import { CrossIcon } from "./icons";
import { activateProfile, useProfiles, type Profile } from "./kid-api";
import { PinSetup } from "./pin-setup";
import { useModal } from "./use-modal";

/**
 * "Who are you?" (T-091): one big tile per kid profile, one tap switches. From the adult app the server wants a PIN to exist
 * first (409 pin-not-set); the picker then asks for one and carries on with the profile that was tapped. From kid mode a
 * switch between kids needs nothing.
 */
export function ProfilePicker({
  householdId,
  kidMode,
  activeProfileId,
  onClose,
}: {
  householdId: string;
  kidMode: boolean;
  activeProfileId: string | null;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const { reloadSession } = useSession();
  const sheet = useRef<HTMLDivElement>(null);
  const headingId = useId();
  const { profiles, loading, failed, reload } = useProfiles(householdId);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<MessageKey | null>(null);
  const [needsPin, setNeedsPin] = useState<Profile | null>(null);

  useModal(sheet, onClose);

  const choose = async (profile: Profile) => {
    if (busy) return;
    setBusy(profile.id);
    setError(null);
    const result = await activateProfile(householdId, profile.id);
    if (result.kind === "ok") {
      const read = await reloadSession();
      setBusy(null);
      if (read) onClose();
      else setError("problem.unreachable");
      return;
    }
    setBusy(null);
    if (result.kind === "pin-not-set") setNeedsPin(profile);
    else setError(result.key);
  };

  return (
    <div className="kid-backdrop">
      <div ref={sheet} className="kid-sheet kid-picker" role="dialog" aria-modal="true" aria-labelledby={headingId} tabIndex={-1}>
        <div className="kid-picker-head">
          <h2 id={headingId}>{t("kid.picker.title")}</h2>
          <button type="button" className="kid-close" aria-label={t("kid.close")} onClick={onClose}>
            <CrossIcon />
          </button>
        </div>

        {needsPin ? (
          <div className="kid-picker-pin" data-testid="picker-needs-pin">
            <p>{t("kid.picker.needsPin")}</p>
            <PinSetup onDone={() => void choose(needsPin).then(() => setNeedsPin(null))} />
          </div>
        ) : (
          <>
            {loading && <p role="status">{t("kid.picker.loading")}</p>}
            {failed && (
              <div role="alert" className="planner-message">
                <p>{t("kid.picker.loadFailed")}</p>
                <button type="button" className="button" onClick={reload}>
                  {t("planner.retry")}
                </button>
              </div>
            )}
            {profiles && profiles.length === 0 && (
              <p className="planner-message" data-testid="picker-empty">
                {t("kid.picker.empty")}
              </p>
            )}
            {profiles && profiles.length > 0 && (
              <ul className="kid-tiles" data-testid="profile-picker">
                {profiles.map((profile) => (
                  <li key={profile.id}>
                    <button
                      type="button"
                      className="kid-tile"
                      aria-current={profile.id === activeProfileId ? "true" : undefined}
                      disabled={busy !== null}
                      aria-label={t("kid.picker.switchTo", { name: profile.displayName })}
                      data-testid="profile-tile"
                      onClick={() => void choose(profile)}
                    >
                      <ProfileAvatar profile={profile} size="lg" />
                      <span className="kid-tile-name">{profile.displayName}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
            {error && (
              <p role="alert" className="form-error">
                {t(error)}
              </p>
            )}
            {!kidMode && (
              <Link className="button button-outline kid-picker-new" to="/household" onClick={onClose}>
                {t("kid.picker.newProfile")}
              </Link>
            )}
          </>
        )}
      </div>
    </div>
  );
}
