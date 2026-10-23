import { useEffect, useRef, useState, type FormEvent } from "react";
import { Navigate, useNavigate, useSearchParams } from "react-router";
import { safeReturnTo } from "../auth-paths";
import { useSession, type SignInResult } from "../session";

const messages = {
  invalid: "Sign in failed. Check your email and password and try again.",
  "rate-limited": "Too many attempts. Wait a minute and try again.",
  error: "Something went wrong. Please try again.",
  "cookie-missing":
    "Signed in, but the browser did not keep the session cookie. Safari does not store Secure cookies on http://localhost; try Chrome or use https.",
} as const;

export default function Login() {
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

  const message = result && result.kind !== "ok" ? messages[result.kind] : null;

  return (
    <main id="main" className="login">
      <h1>Sign in</h1>
      <form onSubmit={onSubmit} aria-busy={submitting}>
        <div className="field">
          <label htmlFor="email">Email</label>
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
          <label htmlFor="password">Password</label>
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
          {submitting ? "Signing in..." : "Sign in"}
        </button>
      </form>
    </main>
  );
}
