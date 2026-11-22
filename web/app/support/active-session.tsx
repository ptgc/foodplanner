import { useEffect, useState } from "react";
import { api } from "../api/client";
import { call } from "../household/call";
import { useI18n, type MessageKey } from "../i18n";
import { categoryKeys, type SupportSession } from "./shared";
import { SupportPanels, type PanelId } from "./support-panels";

const tabs: { id: PanelId; label: MessageKey }[] = [
  { id: "overview", label: "support.tab.overview" },
  { id: "members", label: "support.tab.members" },
  { id: "invites", label: "support.tab.invites" },
  { id: "profiles", label: "support.tab.profiles" },
  { id: "plan", label: "support.tab.plan" },
  { id: "shopping", label: "support.tab.shopping" },
  { id: "recipes", label: "support.tab.recipes" },
  { id: "log", label: "support.tab.log" },
];

/**
 * The open support session (T-096): a banner that always shows the household, the mode, the reason category and the time
 * left, with a button to end it, and the sections of that ONE household. The write actions appear only in a write session.
 * When the time is up the session is treated as ended (the server stops accepting it at the same moment).
 */
export function ActiveSession({ session, onEnded }: { session: SupportSession; onEnded: () => void }) {
  const { t } = useI18n();
  const [now, setNow] = useState(() => Date.now());
  const [panel, setPanel] = useState<PanelId>("overview");
  const [ending, setEnding] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const expiresAt = new Date(session.expiresAt).getTime();
  const remaining = expiresAt - now;

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 15_000);
    return () => window.clearInterval(timer);
  }, []);

  async function end() {
    setEnding(true);
    setError(null);
    const result = await call(() => api.DELETE("/api/support/sessions/current"));
    setEnding(false);
    if (result.ok) onEnded();
    else setError(result.key);
  }

  if (remaining <= 0) {
    return (
      <div className="household-card" role="status">
        <p>{t("support.banner.expired")}</p>
        <button type="button" className="button" onClick={onEnded}>
          {t("support.back")}
        </button>
      </div>
    );
  }

  const minutes = Math.max(1, Math.ceil(remaining / 60_000));
  const write = session.mode === "Write";
  return (
    <>
      <div className={write ? "support-banner support-banner-write" : "support-banner"} role="region" aria-label={t("support.banner.label")}>
        <div>
          <strong data-testid="support-banner">
            {t("support.banner.text", {
              household: session.householdName,
              mode: t(session.mode === "Write" ? "support.mode.Write" : "support.mode.Read"),
              category: t(categoryKeys[session.reasonCategory]),
            })}
          </strong>
          <span className="support-banner-time">{t("support.banner.remaining", { minutes })}</span>
        </div>
        <button type="button" className="button button-light" disabled={ending} onClick={() => void end()}>
          {ending ? t("support.banner.ending") : t("support.banner.end")}
        </button>
      </div>
      {error && (
        <p role="alert" className="form-error">
          {t(error)}
        </p>
      )}
      <nav className="support-tabs" aria-label={t("support.tab.list")}>
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            className={tab.id === panel ? "button" : "button button-outline"}
            aria-current={tab.id === panel ? "page" : undefined}
            onClick={() => setPanel(tab.id)}
          >
            {t(tab.label)}
          </button>
        ))}
      </nav>
      <SupportPanels panel={panel} householdId={session.householdId} householdName={session.householdName} write={write} />
    </>
  );
}
