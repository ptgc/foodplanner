import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link } from "react-router";
import { api } from "../api/client";
import { useI18n } from "../i18n";
import { LanguageSwitch } from "../i18n/language-switch";
import { problemKey, problemTypeOf } from "../i18n/problems";

const rateLimited = "urn:foodplanner:problem:rate-limited";

/** T-072: register records only the email address; the emailed link is where the password is chosen. */
export default function Register() {
  const { t } = useI18n();
  const [email, setEmail] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [sent, setSent] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const emailRef = useRef<HTMLInputElement>(null);
  const alertRef = useRef<HTMLDivElement>(null);
  const sentRef = useRef<HTMLParagraphElement>(null);

  useEffect(() => {
    emailRef.current?.focus();
  }, []);

  useEffect(() => {
    if (problem) alertRef.current?.focus();
  }, [problem]);

  useEffect(() => {
    if (sent) sentRef.current?.focus();
  }, [sent]);

  async function onSubmit(event: FormEvent) {
    event.preventDefault();
    if (submitting) return;
    setSubmitting(true);
    setProblem(null);
    try {
      const { response, error } = await api.POST("/api/auth/register", { body: { email: email.trim() } });
      if (response.ok) {
        setSent(true);
      } else if (response.status === 400 && problemTypeOf(error) === undefined) {
        setProblem(t("register.invalidEmail"));
      } else {
        setProblem(t(problemKey(problemTypeOf(error) ?? (response.status === 429 ? rateLimited : undefined))));
      }
    } catch {
      setProblem(t("problem.unreachable"));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main id="main" className="login">
      <div className="login-language">
        <LanguageSwitch />
      </div>
      <h1>{t("register.title")}</h1>
      {sent ? (
        <p ref={sentRef} tabIndex={-1} role="status" className="auth-note">
          {t("register.sent")}
        </p>
      ) : (
        <form onSubmit={onSubmit} aria-busy={submitting}>
          <p>{t("register.intro")}</p>
          <div className="field">
            <label htmlFor="email">{t("login.email")}</label>
            <input
              id="email"
              ref={emailRef}
              type="email"
              name="email"
              autoComplete="email"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />
          </div>
          <div ref={alertRef} tabIndex={-1} role="alert" className="form-error">
            {problem}
          </div>
          <button type="submit" className="button" disabled={submitting}>
            {submitting ? t("register.submitting") : t("register.submit")}
          </button>
        </form>
      )}
      <p>
        <Link to="/login" className="auth-link">
          {t("register.haveAccount")}
        </Link>
      </p>
    </main>
  );
}
