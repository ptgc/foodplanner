import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link, Navigate, useNavigate, useSearchParams } from "react-router";
import { safeReturnTo } from "../auth-paths";
import { useI18n } from "../i18n";
import { LanguageSwitch } from "../i18n/language-switch";
import { problemKey } from "../i18n/problems";
import { useSession, type SignInResult } from "../session";

export default function Login() {
  const { t } = useI18n();
  const { session, signIn } = useSession();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const returnTo = safeReturnTo(params.get("returnTo"));
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [result, setResult] = useState<SignInResult | null>(null);
  const emailRef = useRef<HTMLInputElement>(null);
  const alertRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    emailRef.current?.focus();
  }, []);

  useEffect(() => {
    if (result && result.kind !== "ok") alertRef.current?.focus();
  }, [result]);

  if (session.status === "signedIn") return <Navigate to={returnTo} replace />;

  async function onSubmit(event: FormEvent) {
    event.preventDefault();
    if (submitting) return;
    setSubmitting(true);
    setResult(null);
    const next = await signIn(email, password);
    setSubmitting(false);
    if (next.kind === "ok") void navigate(returnTo, { replace: true });
    else setResult(next);
  }

  const message =
    result?.kind === "cookie-missing"
      ? t("login.cookieMissing")
      : result?.kind === "problem"
        ? t(problemKey(result.problemType))
        : null;

  return (
    <main id="main" className="login">
      <div className="login-language">
        <LanguageSwitch />
      </div>
      <h1>{t("common.signIn")}</h1>
      {params.get("passwordSet") === "1" && (
        <p role="status" className="auth-note">
          {t("login.passwordSet")}
        </p>
      )}
      <form onSubmit={onSubmit} aria-busy={submitting}>
        <div className="field">
          <label htmlFor="email">{t("login.email")}</label>
          <input
            id="email"
            ref={emailRef}
            type="email"
            name="email"
            autoComplete="username"
            required
            value={email}
            onChange={(e) => setEmail(e.target.value)}
          />
        </div>
        <div className="field">
          <label htmlFor="password">{t("login.password")}</label>
          <input
            id="password"
            type="password"
            name="password"
            autoComplete="current-password"
            required
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </div>
        <div ref={alertRef} tabIndex={-1} role="alert" className="form-error">
          {message}
        </div>
        <button type="submit" className="button" disabled={submitting}>
          {submitting ? t("login.signingIn") : t("common.signIn")}
        </button>
      </form>
      <p>
        <Link to="/register" className="auth-link">
          {t("login.createAccount")}
        </Link>
      </p>
    </main>
  );
}
