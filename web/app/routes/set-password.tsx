import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link, useLocation, useNavigate, useSearchParams } from "react-router";
import { api } from "../api/client";
import { useI18n } from "../i18n";
import { LanguageSwitch } from "../i18n/language-switch";
import { problemKey, problemTypeOf } from "../i18n/problems";

const confirmationInvalid = "urn:foodplanner:problem:confirmation-invalid";
const rateLimited = "urn:foodplanner:problem:rate-limited";

/** The server default (Auth:MinPasswordLength). The server stays the authority; this only saves a round trip. */
const minPasswordLength = 10;

/** T-072: the emailed link opens here. The person chooses the password; that confirms the address. */
export default function SetPassword() {
  const { t } = useI18n();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const location = useLocation();
  const kept = location.state as { userId?: string; token?: string } | null;
  // Taken once from the emailed link, or from history state after the URL was cleaned (a reload keeps that state).
  const [link] = useState(() => ({
    userId: params.get("userId") ?? kept?.userId ?? "",
    token: params.get("token") ?? kept?.token ?? "",
  }));
  const { userId, token } = link;
  const [password, setPassword] = useState("");
  const [repeat, setRepeat] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [linkInvalid, setLinkInvalid] = useState(userId === "" || token === "");
  const passwordRef = useRef<HTMLInputElement>(null);
  const alertRef = useRef<HTMLDivElement>(null);
  const invalidRef = useRef<HTMLParagraphElement>(null);

  useEffect(() => {
    passwordRef.current?.focus();
  }, []);

  // Take the one-time token out of the address bar and the history entry: it would otherwise stay in history, be
  // shown on screen and leak through Referer. The values live in component state and in this entry's history state.
  useEffect(() => {
    if (params.has("userId") || params.has("token")) {
      void navigate("/set-password", { replace: true, state: link });
    }
    // Runs once for the URL the page was opened with.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (message) alertRef.current?.focus();
  }, [message]);

  useEffect(() => {
    if (linkInvalid) invalidRef.current?.focus();
  }, [linkInvalid]);

  async function onSubmit(event: FormEvent) {
    event.preventDefault();
    if (submitting) return;
    if (password.length < minPasswordLength) {
      setMessage(t("setPassword.tooShort", { count: minPasswordLength }));
      return;
    }
    if (password !== repeat) {
      setMessage(t("setPassword.mismatch"));
      return;
    }
    setSubmitting(true);
    setMessage(null);
    try {
      const { response, error } = await api.POST("/api/auth/confirm-email", { body: { userId, token, password } });
      if (response.ok) {
        void navigate("/login?passwordSet=1", { replace: true });
        return;
      }
      const type = problemTypeOf(error);
      if (type === confirmationInvalid) setLinkInvalid(true);
      else if (response.status === 400 && type === undefined) setMessage(t("setPassword.rejected"));
      else setMessage(t(problemKey(type ?? (response.status === 429 ? rateLimited : undefined))));
    } catch {
      setMessage(t("problem.unreachable"));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main id="main" className="login">
      <meta name="referrer" content="no-referrer" />
      <div className="login-language">
        <LanguageSwitch />
      </div>
      <h1>{t("setPassword.title")}</h1>
      {linkInvalid ? (
        <>
          <p ref={invalidRef} tabIndex={-1} role="alert" className="form-error">
            {t("setPassword.invalidLink")}
          </p>
          <p>
            <Link to="/register" className="button">
              {t("setPassword.registerAgain")}
            </Link>
          </p>
        </>
      ) : (
        <form onSubmit={onSubmit} aria-busy={submitting}>
          <div className="field">
            <label htmlFor="password">{t("setPassword.password")}</label>
            <input
              id="password"
              ref={passwordRef}
              type="password"
              name="password"
              autoComplete="new-password"
              aria-describedby="password-rules"
              required
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
            <small id="password-rules">{t("setPassword.rules", { count: minPasswordLength })}</small>
          </div>
          <div className="field">
            <label htmlFor="repeat">{t("setPassword.confirm")}</label>
            <input
              id="repeat"
              type="password"
              name="repeat"
              autoComplete="new-password"
              required
              value={repeat}
              onChange={(e) => setRepeat(e.target.value)}
            />
          </div>
          <div ref={alertRef} tabIndex={-1} role="alert" className="form-error">
            {message}
          </div>
          <button type="submit" className="button" disabled={submitting}>
            {submitting ? t("setPassword.submitting") : t("setPassword.submit")}
          </button>
        </form>
      )}
    </main>
  );
}
