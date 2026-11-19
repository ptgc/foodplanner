import { useId, useState, type FormEvent } from "react";
import "../kid/kid.css";
import { api } from "../api/client";
import { useI18n, type MessageKey } from "../i18n";
import { ProfileAvatar } from "../kid/avatar";
import { AVATARS, avatarEmoji, avatarTint } from "../kid/avatars";
import { useProfiles, type Profile } from "../kid/kid-api";
import { PinSetup } from "../kid/pin-setup";
import { call } from "./call";
import { ConfirmDialog } from "./confirm-dialog";

/** Longest profile name the API accepts (T-026) and the most profiles a household can have. */
const maxNameLength = 30;
const maxProfiles = 10;

/**
 * Kid profiles and the grown-up's own PIN (T-091, Adult and Owner). A kid profile is a name and an avatar; the PIN is what
 * takes the shared device back from kid mode. The API decides everything; this only offers the controls.
 */
export function KidProfiles({ householdId }: { householdId: string }) {
  const { t } = useI18n();
  const { profiles, failed, reload } = useProfiles(householdId);
  const [editing, setEditing] = useState<Profile | "new" | null>(null);
  const [removing, setRemoving] = useState<Profile | null>(null);
  const [removeBusy, setRemoveBusy] = useState(false);
  const [removeError, setRemoveError] = useState<MessageKey | null>(null);
  const [pinOpen, setPinOpen] = useState(false);
  const [pinSaved, setPinSaved] = useState(false);

  const remove = async () => {
    if (!removing || removeBusy) return;
    setRemoveBusy(true);
    setRemoveError(null);
    const result = await call(() =>
      api.DELETE("/api/households/{householdId}/profiles/{profileId}", {
        params: { path: { householdId, profileId: removing.id } },
      }),
    );
    setRemoveBusy(false);
    if (result.ok || result.status === 404) {
      setRemoving(null);
      reload();
    } else setRemoveError(result.key);
  };

  return (
    <>
      <div className="household-card" data-testid="kid-profiles">
        <h2>{t("kid.profiles.title")}</h2>
        <p>{t("kid.profiles.body")}</p>
        {failed && (
          <p role="alert" className="form-error">
            {t("kid.picker.loadFailed")}
          </p>
        )}
        {profiles && profiles.length === 0 && <p className="household-note">{t("kid.profiles.none")}</p>}
        {profiles && profiles.length > 0 && (
          <ul className="kid-profile-list">
            {profiles.map((profile) => (
              <li key={profile.id} data-testid="kid-profile-row">
                <ProfileAvatar profile={profile} size="sm" />
                <span className="kid-profile-name">{profile.displayName}</span>
                <button type="button" className="button button-outline" onClick={() => setEditing(profile)}>
                  {t("kid.profiles.edit")}
                  <span className="visually-hidden"> {profile.displayName}</span>
                </button>
                <button type="button" className="button button-outline" onClick={() => setRemoving(profile)}>
                  {t("kid.profiles.delete")}
                  <span className="visually-hidden"> {profile.displayName}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
        {editing === null && (
          <button
            type="button"
            className="button"
            disabled={(profiles?.length ?? 0) >= maxProfiles}
            onClick={() => setEditing("new")}
            data-testid="kid-profile-add"
          >
            {t("kid.profiles.add")}
          </button>
        )}
        {editing !== null && (
          <ProfileForm
            key={editing === "new" ? "new" : editing.id}
            householdId={householdId}
            profile={editing === "new" ? null : editing}
            onDone={() => {
              setEditing(null);
              reload();
            }}
            onCancel={() => setEditing(null)}
          />
        )}
      </div>

      <div className="household-card" data-testid="kid-pin-card">
        <h2>{t("kid.pinCard.title")}</h2>
        <p>{t("kid.pinCard.body")}</p>
        {pinSaved && (
          <p role="status" className="household-notice" data-testid="pin-saved">
            {t("kid.pinCard.saved")}
          </p>
        )}
        {pinOpen ? (
          <PinSetup
            onDone={() => {
              setPinOpen(false);
              setPinSaved(true);
            }}
          />
        ) : (
          <button
            type="button"
            className="button"
            data-testid="pin-open"
            onClick={() => {
              setPinSaved(false);
              setPinOpen(true);
            }}
          >
            {t("kid.pinCard.set")}
          </button>
        )}
      </div>

      {removing && (
        <ConfirmDialog
          title={t("kid.profiles.deleteTitle", { name: removing.displayName })}
          body={t("kid.profiles.deleteBody")}
          confirmLabel={t("kid.profiles.delete")}
          busy={removeBusy}
          error={removeError}
          onConfirm={() => void remove()}
          onCancel={() => setRemoving(null)}
        />
      )}
    </>
  );
}

function ProfileForm({
  householdId,
  profile,
  onDone,
  onCancel,
}: {
  householdId: string;
  profile: Profile | null;
  onDone: () => void;
  onCancel: () => void;
}) {
  const { t } = useI18n();
  const nameId = useId();
  const [name, setName] = useState(profile?.displayName ?? "");
  const [avatar, setAvatar] = useState<string>(profile?.avatar ?? AVATARS[0][0]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const trimmed = name.trim();
    if (busy) return;
    if (trimmed === "") {
      setError("kid.profiles.nameRequired");
      return;
    }
    setBusy(true);
    setError(null);
    const body = { displayName: trimmed, avatar };
    const result = await call(() =>
      profile
        ? api.PUT("/api/households/{householdId}/profiles/{profileId}", {
            params: { path: { householdId, profileId: profile.id } },
            body,
          })
        : api.POST("/api/households/{householdId}/profiles", { params: { path: { householdId } }, body }),
    );
    setBusy(false);
    if (result.ok) onDone();
    else setError(result.key);
  };

  return (
    <form className="kid-profile-form" onSubmit={(e) => void submit(e)} aria-busy={busy} data-testid="kid-profile-form">
      <div className="field">
        <label htmlFor={nameId}>{t("kid.profiles.name")}</label>
        <input id={nameId} type="text" maxLength={maxNameLength} value={name} onChange={(e) => setName(e.target.value)} autoComplete="off" />
      </div>
      <fieldset className="kid-avatar-choice">
        <legend>{t("kid.profiles.avatar")}</legend>
        {AVATARS.map(([key]) => (
          <label key={key} className="kid-avatar-option">
            <input type="radio" name="avatar" value={key} checked={avatar === key} onChange={() => setAvatar(key)} />
            <span className="kid-avatar kid-avatar-md" style={{ backgroundColor: avatarTint(key) }} aria-hidden="true">
              {avatarEmoji(key)}
            </span>
            <span className="visually-hidden">{t(`kid.avatar.${key}`)}</span>
          </label>
        ))}
      </fieldset>
      {error && (
        <p role="alert" className="form-error" data-testid="kid-profile-error">
          {t(error)}
        </p>
      )}
      <div className="kid-form-buttons">
        <button type="submit" className="button" disabled={busy}>
          {profile ? t("kid.profiles.save") : t("kid.profiles.create")}
        </button>
        <button type="button" className="button button-outline" onClick={onCancel}>
          {t("kid.profiles.cancel")}
        </button>
      </div>
    </form>
  );
}
