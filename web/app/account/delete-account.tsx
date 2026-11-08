import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link, Navigate } from "react-router";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { call } from "../household/call";
import { useLoaded } from "../household/use-loaded";
import { powers } from "../household/roles";
import "../household/household.css";
import { useI18n, type MessageKey } from "../i18n";
import { problemKey, problemTypeOf } from "../i18n/problems";
import { useSession } from "../session";

type Preview = components["schemas"]["AccountDeletionPreviewResponse"];
type HouseholdInfo = components["schemas"]["AccountHouseholdResponse"];

const problemPrefix = "urn:foodplanner:problem:";
const passwordIncorrect = `${problemPrefix}password-incorrect`;
const ownsHouseholds = `${problemPrefix}account-owns-households`;
const deletionChanged = `${problemPrefix}account-deletion-changed`;
const kidModeProblem = `${problemPrefix}kid-mode`;

type Failure = { key: MessageKey; params?: Record<string, string | number> };

/** The typed text matches the account email: case and surrounding spaces are ignored (the API does the same). */
export function confirmationMatches(typed: string, expected: string): boolean {
  const wanted = expected.trim().toLowerCase();
  return wanted !== "" && typed.trim().toLowerCase() === wanted;
}

/** Retry-After as whole seconds (the header may also be an HTTP date, which we do not use). */
function retryAfterSeconds(response: Response): number | null {
  const value = Number(response.headers.get("Retry-After"));
  return Number.isFinite(value) && value > 0 ? Math.ceil(value) : null;
}

export default function DeleteAccount() {
  const { session } = useSession();
  // Kid mode never reaches this screen (the shell shows the kid app); if it does, go home. The API refuses anyway.
  if (session.status !== "signedIn" || session.user.kidMode) return <Navigate to="/" replace />;
  return <Screen />;
}

function Screen() {
  const { t } = useI18n();
  const { session, accountDeleted, setActiveHousehold } = useSession();
  const { data: preview, error: loadError, reload: load } = useLoaded<Preview>("preview", () =>
    call(() => api.GET("/api/account/deletion-preview")),
  );
  const [password, setPassword] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<Failure | null>(null);
  const passwordRef = useRef<HTMLInputElement>(null);
  const alertRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (failure) alertRef.current?.focus();
  }, [failure]);

  if (session.status !== "signedIn") return null;
  const roleOf = (id: string) => session.households.find((h) => h.id === id)?.role;
  const open = (id: string) => setActiveHousehold(id);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!preview || busy || !canSubmit(preview)) return;
    setBusy(true);
    setFailure(null);
    try {
      const { response, error } = await api.POST("/api/account/delete", {
        body: { password, confirmation, householdIds: preview.householdsToDelete.map((h) => h.id) },
      });
      if (response.ok) {
        await accountDeleted();
        return;
      }
      setFailure(await failureFor(response, error));
    } catch {
      setFailure({ key: "problem.unreachable" });
    } finally {
      setBusy(false);
    }
  }

  async function failureFor(response: Response, error: unknown): Promise<Failure> {
    const type = problemTypeOf(error);
    if (response.status === 403 && type === passwordIncorrect) {
      setPassword("");
      passwordRef.current?.focus();
      return { key: "account.delete.problem.passwordIncorrect" };
    }
    if (response.status === 403 && type === kidModeProblem) return { key: "account.delete.problem.kidMode" };
    if (response.status === 409 && type === ownsHouseholds) {
      load();
      return { key: "account.delete.problem.ownsHouseholds" };
    }
    if (response.status === 409 && type === deletionChanged) {
      load();
      return { key: "account.delete.changed" };
    }
    if (response.status === 429) {
      const seconds = retryAfterSeconds(response);
      return seconds === null
        ? { key: "account.delete.problem.rateLimitedNoTime" }
        : { key: "account.delete.problem.rateLimited", params: { seconds } };
    }
    if (response.status === 400) return { key: "account.delete.problem.validation" };
    if (response.status === 401) return { key: "problem.unauthenticated" };
    const typed = problemKey(type);
    return { key: typed === "problem.generic" && response.status === 403 ? "problem.forbidden" : typed };
  }

  function canSubmit(p: Preview) {
    return p.canDelete && password !== "" && confirmationMatches(confirmation, p.confirmationText);
  }

  const exportable = preview
    ? [...preview.householdsToDelete, ...preview.blockingHouseholds, ...preview.householdsKept].filter((h) => {
        const role = roleOf(h.id);
        return role !== undefined && powers(role).export;
      })
    : [];

  return (
    <section className="household account-delete" aria-labelledby="account-delete-title">
      <h1 id="account-delete-title">{t("account.delete.title")}</h1>

      {!preview && !loadError && <p role="status">{t("account.delete.loading")}</p>}
      {loadError && !preview && (
        <div className="household-card">
          <p role="alert" className="form-error">
            {t("account.delete.loadFailed")} {t(loadError)}
          </p>
          <button type="button" className="button" onClick={load}>
            {t("account.delete.retry")}
          </button>
        </div>
      )}

      {preview && (
        <>
          <p>{t("account.delete.intro")}</p>

          {failure && (
            <div ref={alertRef} role="alert" tabIndex={-1} className="form-error" data-testid="delete-error">
              {t(failure.key, failure.params)}
            </div>
          )}

          {preview.blockingHouseholds.length > 0 && (
            <div className="household-card household-danger" data-testid="blocking-households">
              <h2>{t("account.delete.blocking.title")}</h2>
              <p>{t("account.delete.blocking.body")}</p>
              <ul className="household-list">
                {preview.blockingHouseholds.map((h) => (
                  <li key={h.id} className="household-row" data-testid="blocking-household">
                    <HouseholdLine household={h} />
                    <Link className="button button-outline household-link" to="/household" onClick={() => open(h.id)}>
                      {t("account.delete.blocking.link", { name: h.name })}
                    </Link>
                  </li>
                ))}
              </ul>
            </div>
          )}

          {preview.householdsToDelete.length > 0 && (
            <div className="household-card" data-testid="households-to-delete">
              <h2>{t("account.delete.toDelete.title")}</h2>
              <p>{t("account.delete.toDelete.body")}</p>
              <ul className="household-list">
                {preview.householdsToDelete.map((h) => (
                  <li key={h.id} className="household-row" data-testid="delete-household">
                    <HouseholdLine household={h} />
                  </li>
                ))}
              </ul>
            </div>
          )}

          {preview.householdsKept.length > 0 && (
            <div className="household-card" data-testid="households-kept">
              <h2>{t("account.delete.kept.title")}</h2>
              <p>{t("account.delete.kept.body")}</p>
              <ul className="household-list">
                {preview.householdsKept.map((h) => (
                  <li key={h.id} className="household-row" data-testid="kept-household">
                    <HouseholdLine household={h} />
                  </li>
                ))}
              </ul>
            </div>
          )}

          {exportable.length > 0 && (
            <div className="household-card" data-testid="export-links">
              <h2>{t("account.delete.export.title")}</h2>
              <p>{t("account.delete.export.body")}</p>
              <ul className="household-list">
                {exportable.map((h) => (
                  <li key={h.id} className="household-row">
                    <Link className="button button-outline household-link" to="/household" onClick={() => open(h.id)}>
                      {t("account.delete.export.link", { name: h.name })}
                    </Link>
                  </li>
                ))}
              </ul>
            </div>
          )}

          {preview.canDelete && (
            <form className="household-card household-danger" onSubmit={(e) => void submit(e)} aria-busy={busy} data-testid="delete-form">
              <h2>{t("account.delete.form.title")}</h2>
              <div className="field">
                <label htmlFor="delete-password">{t("account.delete.password")}</label>
                <input
                  id="delete-password"
                  ref={passwordRef}
                  type="password"
                  name="password"
                  autoComplete="current-password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                />
              </div>
              <div className="field">
                <label htmlFor="delete-confirmation">{t("account.delete.confirmation", { email: preview.confirmationText })}</label>
                <input
                  id="delete-confirmation"
                  type="text"
                  name="confirmation"
                  inputMode="email"
                  autoComplete="off"
                  autoCapitalize="none"
                  spellCheck={false}
                  value={confirmation}
                  onChange={(e) => setConfirmation(e.target.value)}
                />
              </div>
              <button type="submit" className="button button-danger" disabled={busy || !canSubmit(preview)} data-testid="delete-submit">
                {busy ? t("account.delete.deleting") : t("account.delete.button")}
              </button>
            </form>
          )}
        </>
      )}

      <p>
        <Link className="button button-outline household-link" to="/household">
          {t("account.delete.back")}
        </Link>
      </p>
    </section>
  );
}

function HouseholdLine({ household }: { household: HouseholdInfo }) {
  const { t } = useI18n();
  return (
    <span>
      <strong>{household.name}</strong>{" "}
      <span className="household-row-sub">{t("account.delete.members", { count: Number(household.memberCount) })}</span>
    </span>
  );
}
