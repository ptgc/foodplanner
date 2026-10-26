import { useState } from "react";
import { Link, useParams } from "react-router";
import { api } from "../api/client";
import { call } from "../household/call";
import { roleKeys, type Role } from "../household/roles";
import { useI18n, type MessageKey } from "../i18n";
import { useSession } from "../session";

type State =
  | { kind: "idle" }
  | { kind: "joining" }
  | { kind: "joined"; role: Role }
  | { kind: "failed"; key: MessageKey };

/**
 * /invite/{token}: the page behind an invitation link (T-011 bearer link). It lives inside the app shell, so a
 * signed-out visitor is sent to /login?returnTo=/invite/{token} and lands here after signing in. Joining is an explicit
 * button press, never a side effect of opening the page (link previews and reloads must not use up a single-use link).
 */
export default function Invite() {
  const { t } = useI18n();
  const { refreshHouseholds } = useSession();
  const { token } = useParams();
  const [state, setState] = useState<State>({ kind: "idle" });

  if (!token) return null;

  const join = async () => {
    if (state.kind === "joining") return;
    setState({ kind: "joining" });
    const result = await call(() => api.POST("/api/invites/{token}/accept", { params: { path: { token } } }));
    if (!result.ok || !result.data) {
      setState({ kind: "failed", key: result.ok ? "problem.generic" : result.key });
      return;
    }
    const joined = result.data;
    setState({ kind: "joined", role: joined.role });
    // Make the new household the active one so the other screens show it straight away.
    await refreshHouseholds(joined.householdId);
  };

  return (
    <section className="household" aria-labelledby="invite-title">
      <h1 id="invite-title">{t("invite.title")}</h1>
      {state.kind === "joined" ? (
        <>
          <p role="status">{t("invite.joined", { role: t(roleKeys[state.role]) })}</p>
          <Link className="button household-link" to="/household">
            {t("invite.open")}
          </Link>
        </>
      ) : (
        <>
          <p>{t("invite.body")}</p>
          {state.kind === "failed" && (
            <p role="alert" className="form-error">
              {t(state.key)}
            </p>
          )}
          <button type="button" className="button" disabled={state.kind === "joining"} onClick={() => void join()}>
            {state.kind === "joining" ? t("invite.joining") : t("invite.accept")}
          </button>
          {state.kind === "failed" && (
            <p>
              <Link className="auth-link" to="/household">
                {t("invite.open")}
              </Link>
            </p>
          )}
        </>
      )}
    </section>
  );
}
