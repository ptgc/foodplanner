import { useCallback, useState, type FormEvent } from "react";
import { Link } from "react-router";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { call } from "../household/call";
import { ConfirmDialog } from "../household/confirm-dialog";
import { useLoaded } from "../household/use-loaded";
import { allRoles, inviteLink, invitableRoles, powers, roleKeys, type Role } from "../household/roles";
import { useI18n, type MessageKey, type Params } from "../i18n";
import { copyText, shareText } from "../shopping/export";
import { useSession } from "../session";

type Household = components["schemas"]["HouseholdResponse"];
type Member = components["schemas"]["MemberResponse"];
type OpenInvite = components["schemas"]["OpenInviteResponse"];

type Notice = { key: MessageKey; params?: Params };
type Notify = (notice: Notice) => void;

export default function HouseholdPage() {
  const { t } = useI18n();
  const { session } = useSession();
  const [notice, setNotice] = useState<Notice | null>(null);
  if (session.status !== "signedIn") return null;
  const active = session.households.find((h) => h.id === session.activeHouseholdId);

  return (
    <section className="household" aria-labelledby="household-title">
      <h1 id="household-title">{t("household.title")}</h1>
      {notice && (
        <p role="status" className="household-notice" data-testid="household-notice">
          {t(notice.key, notice.params)}
        </p>
      )}
      {active ? (
        <HouseholdSettings key={active.id} household={active} onNotice={setNotice} />
      ) : (
        <div className="household-card">
          <h2>{t("household.noneTitle")}</h2>
          <p>{t("household.noneBody")}</p>
        </div>
      )}
      <CreateHousehold onNotice={setNotice} />
    </section>
  );
}

function HouseholdSettings({ household, onNotice }: { household: Household; onNotice: Notify }) {
  const { t } = useI18n();
  const can = powers(household.role);

  return (
    <>
      <div className="household-card">
        <dl className="household-facts">
          <div>
            <dt>{t("household.name")}</dt>
            <dd data-testid="household-settings-name">{household.name}</dd>
          </div>
          <div>
            <dt>{t("household.yourRole")}</dt>
            <dd data-testid="household-role">{t(roleKeys[household.role])}</dd>
          </div>
        </dl>
      </div>
      {can.manage ? (
        <>
          <Members household={household} onNotice={onNotice} />
          <Invites household={household} />
        </>
      ) : (
        <p className="household-note">{t("household.ownerOnlyNote")}</p>
      )}
      {can.ingredients && (
        <div className="household-card">
          <h2>{t("ingredients.title")}</h2>
          <p>{t("ingredients.linkBody")}</p>
          <Link className="button button-outline" to="/ingredients">
            {t("ingredients.link")}
          </Link>
        </div>
      )}
      {can.export && <Export household={household} onNotice={onNotice} />}
      <Leave household={household} onNotice={onNotice} />
      {can.manage && <Delete household={household} onNotice={onNotice} />}
    </>
  );
}

function useDateFormat() {
  const { locale } = useI18n();
  return useCallback((iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(iso)), [locale]);
}

// ---- Members (Owner) ----

function Members({ household, onNotice }: { household: Household; onNotice: Notify }) {
  const { t } = useI18n();
  const { session } = useSession();
  const formatDate = useDateFormat();
  const [rowError, setRowError] = useState<{ userId: string; key: MessageKey } | null>(null);
  const [pending, setPending] = useState<string | null>(null);
  const [removing, setRemoving] = useState<Member | null>(null);
  const [removeError, setRemoveError] = useState<MessageKey | null>(null);
  const [removeBusy, setRemoveBusy] = useState(false);
  const householdId = household.id;
  const me = session.status === "signedIn" ? session.user.userId : "";

  const { data: members, error: loadError, reload: load, setData: setMembers } = useLoaded<Member[]>(householdId, () =>
    call(() => api.GET("/api/households/{householdId}/members", { params: { path: { householdId } } })),
  );

  const changeRole = async (member: Member, role: Role) => {
    if (pending || role === member.role) return;
    setPending(member.userId);
    setRowError(null);
    const result = await call(() =>
      api.PUT("/api/households/{householdId}/members/{userId}/role", {
        params: { path: { householdId, userId: member.userId } },
        body: { role },
      }),
    );
    setPending(null);
    if (result.ok && result.data) {
      const updated = result.data;
      setMembers((list) => list?.map((m) => (m.userId === updated.userId ? updated : m)) ?? list);
      onNotice({ key: "household.members.roleChanged", params: { email: member.email ?? member.userId, role: t(roleKeys[role]) } });
    } else if (!result.ok) {
      // Demoting the last Owner uses the same problem as leaving; here the advice is about promoting first.
      setRowError({ userId: member.userId, key: result.key === "household.problem.lastOwner" ? "household.problem.lastOwnerDemote" : result.key });
      if (result.status === 404) load();
    }
  };

  const confirmRemove = async () => {
    if (!removing || removeBusy) return;
    setRemoveBusy(true);
    setRemoveError(null);
    const target = removing;
    const result = await call(() =>
      api.DELETE("/api/households/{householdId}/members/{userId}", { params: { path: { householdId, userId: target.userId } } }),
    );
    setRemoveBusy(false);
    if (result.ok) {
      setRemoving(null);
      setMembers((list) => list?.filter((m) => m.userId !== target.userId) ?? list);
      onNotice({ key: "household.members.removed", params: { email: target.email ?? target.userId } });
    } else {
      setRemoveError(result.key);
      if (result.status === 404) load();
    }
  };

  return (
    <div className="household-card">
      <h2>{t("household.members.title")}</h2>
      {loadError && (
        <p role="alert" className="form-error">
          {t(loadError)}
        </p>
      )}
      {!members && !loadError && <p role="status">{t("household.members.loading")}</p>}
      {members && (
        <ul className="household-list" data-testid="member-list">
          {members.map((m) => {
            const label = m.email ?? m.userId;
            const isMe = m.userId === me;
            return (
              <li key={m.userId} className="household-row" data-testid="member-row">
                <div className="household-row-main">
                  <span className="household-row-title">{label}</span>
                  {isMe && <span className="household-badge">{t("household.members.you")}</span>}
                  <span className="household-row-sub">{t("household.members.joined", { date: formatDate(m.joinedAt) })}</span>
                </div>
                {isMe ? (
                  <span className="household-row-role">{t(roleKeys[m.role])}</span>
                ) : (
                  <div className="household-row-actions">
                    <select
                      aria-label={t("household.members.roleOf", { email: label })}
                      value={m.role}
                      disabled={pending === m.userId}
                      onChange={(e) => void changeRole(m, e.target.value as Role)}
                    >
                      {allRoles.map((r) => (
                        <option key={r} value={r}>
                          {t(roleKeys[r])}
                        </option>
                      ))}
                    </select>
                    {m.role !== "Owner" && (
                      <button
                        type="button"
                        className="button button-outline"
                        aria-label={t("household.members.removeFor", { email: label })}
                        onClick={() => {
                          setRemoveError(null);
                          setRemoving(m);
                        }}
                      >
                        {t("household.members.remove")}
                      </button>
                    )}
                  </div>
                )}
                {rowError?.userId === m.userId && (
                  <p role="alert" className="form-error household-row-error">
                    {t(rowError.key)}
                  </p>
                )}
              </li>
            );
          })}
        </ul>
      )}
      {removing && (
        <ConfirmDialog
          title={t("household.members.removeTitle", { email: removing.email ?? removing.userId })}
          body={t("household.members.removeBody")}
          confirmLabel={t("household.members.removeConfirm")}
          busy={removeBusy}
          error={removeError}
          onConfirm={() => void confirmRemove()}
          onCancel={() => setRemoving(null)}
        />
      )}
    </div>
  );
}

// ---- Invitations (Owner) ----

function Invites({ household }: { household: Household }) {
  const { t } = useI18n();
  const formatDate = useDateFormat();
  const householdId = household.id;
  const [role, setRole] = useState<Role>("Adult");
  const [error, setError] = useState<MessageKey | null>(null);
  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<{ link: string; role: Role } | null>(null);
  const [copyStatus, setCopyStatus] = useState<"copied" | "manual" | null>(null);
  const [revoking, setRevoking] = useState<OpenInvite | null>(null);
  const [revokeError, setRevokeError] = useState<MessageKey | null>(null);
  const [revokeBusy, setRevokeBusy] = useState(false);
  const [revokedNotice, setRevokedNotice] = useState(false);

  const { data: invites, error: loadError, reload: load } = useLoaded<OpenInvite[]>(householdId, () =>
    call(() => api.GET("/api/households/{householdId}/invites", { params: { path: { householdId } } })),
  );

  const create = async () => {
    if (creating) return;
    setCreating(true);
    setError(null);
    setCopyStatus(null);
    setRevokedNotice(false);
    const result = await call(() =>
      api.POST("/api/households/{householdId}/invites", { params: { path: { householdId } }, body: { role } }),
    );
    setCreating(false);
    if (result.ok && result.data) {
      setCreated({ link: inviteLink(result.data.token), role: result.data.role });
      load();
    } else if (!result.ok) setError(result.key);
  };

  const copy = async () => {
    if (!created) return;
    setCopyStatus((await copyText(created.link)) ? "copied" : "manual");
  };

  const share = async () => {
    if (!created) return;
    const outcome = await shareText({
      title: t("household.invites.shareTitle", { name: household.name }),
      // The link goes in the text: some share targets drop a separate url field.
      text: `${t("household.invites.shareText", { name: household.name })} ${created.link}`,
    });
    if (outcome === "unavailable") await copy();
  };

  const confirmRevoke = async () => {
    if (!revoking || revokeBusy) return;
    setRevokeBusy(true);
    setRevokeError(null);
    const target = revoking;
    const result = await call(() =>
      api.DELETE("/api/households/{householdId}/invites/{inviteId}", { params: { path: { householdId, inviteId: target.id } } }),
    );
    setRevokeBusy(false);
    if (result.ok) {
      setRevoking(null);
      setRevokedNotice(true);
      load();
    } else {
      setRevokeError(result.key);
      if (result.status === 404) load();
    }
  };

  const canShare = typeof navigator !== "undefined" && typeof navigator.share === "function";

  return (
    <div className="household-card">
      <h2>{t("household.invites.title")}</h2>
      <div className="household-inline">
        <div className="field">
          <label htmlFor="invite-role">{t("household.invites.role")}</label>
          <select id="invite-role" value={role} onChange={(e) => setRole(e.target.value as Role)}>
            {invitableRoles.map((r) => (
              <option key={r} value={r}>
                {t(roleKeys[r])}
              </option>
            ))}
          </select>
        </div>
        <button type="button" className="button" disabled={creating} onClick={() => void create()}>
          {t("household.invites.create")}
        </button>
      </div>
      {(error ?? loadError) && (
        <p role="alert" className="form-error">
          {t((error ?? loadError) as MessageKey)}
        </p>
      )}
      {created && (
        <div className="household-invite-created" data-testid="invite-created">
          <p role="status">{t("household.invites.created")}</p>
          <div className="field">
            <label htmlFor="invite-link">{t("household.invites.link")}</label>
            <input
              id="invite-link"
              type="text"
              readOnly
              value={created.link}
              onFocus={(e) => e.currentTarget.select()}
              data-testid="invite-link"
            />
          </div>
          <div className="household-actions">
            <button type="button" className="button" onClick={() => void copy()}>
              {t("household.invites.copy")}
            </button>
            {canShare && (
              <button type="button" className="button button-outline" onClick={() => void share()}>
                {t("household.invites.share")}
              </button>
            )}
            <button type="button" className="button button-outline" onClick={() => setCreated(null)}>
              {t("household.invites.dismiss")}
            </button>
          </div>
          <p role="status" aria-live="polite" data-testid="copy-status">
            {copyStatus === "copied" && t("household.invites.copied")}
            {copyStatus === "manual" && t("household.invites.copyManually")}
          </p>
        </div>
      )}
      <h3>{t("household.invites.open")}</h3>
      {revokedNotice && <p role="status">{t("household.invites.revoked")}</p>}
      {invites && invites.length === 0 && <p>{t("household.invites.none")}</p>}
      {invites && invites.length > 0 && (
        <ul className="household-list" data-testid="invite-list">
          {invites.map((invite) => {
            const roleLabel = t(roleKeys[invite.role]);
            const date = formatDate(invite.expiresAt);
            return (
              <li key={invite.id} className="household-row" data-testid="invite-row">
                <div className="household-row-main">
                  <span className="household-row-title">{t("household.invites.item", { role: roleLabel, date })}</span>
                </div>
                <div className="household-row-actions">
                  <button
                    type="button"
                    className="button button-outline"
                    aria-label={t("household.invites.revokeFor", { role: roleLabel, date })}
                    onClick={() => {
                      setRevokeError(null);
                      setRevoking(invite);
                    }}
                  >
                    {t("household.invites.revoke")}
                  </button>
                </div>
              </li>
            );
          })}
        </ul>
      )}
      {revoking && (
        <ConfirmDialog
          title={t("household.invites.revokeTitle")}
          body={t("household.invites.revokeBody")}
          confirmLabel={t("household.invites.revokeConfirm")}
          busy={revokeBusy}
          error={revokeError}
          onConfirm={() => void confirmRevoke()}
          onCancel={() => setRevoking(null)}
        />
      )}
    </div>
  );
}

// ---- Export (Owner, Adult) ----

function Export({ household, onNotice }: { household: Household; onNotice: Notify }) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);

  const download = async () => {
    if (busy) return;
    setBusy(true);
    setError(null);
    const result = await call(
      () =>
        api.GET("/api/households/{householdId}/export", {
          params: { path: { householdId: household.id } },
          parseAs: "text",
        }),
      { 429: "household.problem.exportRateLimited" },
    );
    setBusy(false);
    if (!result.ok) {
      setError(result.key);
      return;
    }
    const blob = new Blob([String(result.data ?? "")], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `foodplanner-household-${household.id.replaceAll("-", "")}.json`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
    onNotice({ key: "household.export.done" });
  };

  return (
    <div className="household-card">
      <h2>{t("household.export.title")}</h2>
      <p>{t("household.export.body")}</p>
      {error && (
        <p role="alert" className="form-error">
          {t(error)}
        </p>
      )}
      <button type="button" className="button" disabled={busy} onClick={() => void download()}>
        {t("household.export.button")}
      </button>
    </div>
  );
}

// ---- Leave (every member) and delete (Owner) ----

function Leave({ household, onNotice }: { household: Household; onNotice: Notify }) {
  const { t } = useI18n();
  const { refreshHouseholds } = useSession();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);

  const confirm = async () => {
    if (busy) return;
    setBusy(true);
    setError(null);
    const result = await call(() => api.POST("/api/households/{householdId}/leave", { params: { path: { householdId: household.id } } }));
    if (!result.ok) {
      setBusy(false);
      setError(result.key);
      return;
    }
    // The server has left and closed this household's live connections. Re-read the list: this screen then shows the
    // next household (or the empty state) and this component unmounts.
    onNotice({ key: "household.leave.done", params: { name: household.name } });
    const refreshed = await refreshHouseholds();
    if (!refreshed) {
      setBusy(false);
      setError("problem.unreachable");
    }
  };

  return (
    <div className="household-card">
      <h2>{t("household.leave.title")}</h2>
      <p>{t("household.leave.body")}</p>
      {household.role === "Owner" && <p className="household-note">{t("household.leave.hint")}</p>}
      <button type="button" className="button button-outline" onClick={() => setOpen(true)}>
        {t("household.leave.button")}
      </button>
      {open && (
        <ConfirmDialog
          title={t("household.leave.confirmTitle", { name: household.name })}
          body={t("household.leave.confirmBody")}
          confirmLabel={t("household.leave.confirm")}
          busy={busy}
          error={error}
          onConfirm={() => void confirm()}
          onCancel={() => setOpen(false)}
        />
      )}
    </div>
  );
}

function Delete({ household, onNotice }: { household: Household; onNotice: Notify }) {
  const { t } = useI18n();
  const { refreshHouseholds } = useSession();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);

  const confirm = async () => {
    if (busy) return;
    setBusy(true);
    setError(null);
    const result = await call(() => api.DELETE("/api/households/{householdId}", { params: { path: { householdId: household.id } } }));
    if (!result.ok) {
      setBusy(false);
      setError(result.key);
      return;
    }
    onNotice({ key: "household.delete.done", params: { name: household.name } });
    const refreshed = await refreshHouseholds();
    if (!refreshed) {
      setBusy(false);
      setError("problem.unreachable");
    }
  };

  return (
    <div className="household-card household-danger">
      <h2>{t("household.delete.title")}</h2>
      <p>{t("household.delete.body")}</p>
      <button type="button" className="button button-danger" onClick={() => setOpen(true)}>
        {t("household.delete.button")}
      </button>
      {open && (
        <ConfirmDialog
          title={t("household.delete.confirmTitle", { name: household.name })}
          body={t("household.delete.confirmBody")}
          confirmLabel={t("household.delete.confirm")}
          requireText={household.name}
          requireTextLabel={t("household.delete.typeLabel", { name: household.name })}
          busy={busy}
          error={error}
          onConfirm={() => void confirm()}
          onCancel={() => setOpen(false)}
        />
      )}
    </div>
  );
}

// ---- Create another household ----

function CreateHousehold({ onNotice }: { onNotice: Notify }) {
  const { t } = useI18n();
  const { refreshHouseholds } = useSession();
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const trimmed = name.trim();
    if (busy || !trimmed) return;
    setBusy(true);
    setError(null);
    const result = await call(() => api.POST("/api/households", { body: { name: trimmed } }));
    setBusy(false);
    if (!result.ok || !result.data) {
      setError(result.ok ? "problem.generic" : result.key);
      return;
    }
    const created = result.data;
    setName("");
    onNotice({ key: "household.create.done", params: { name: created.name } });
    await refreshHouseholds(created.id);
  };

  return (
    <form className="household-card" onSubmit={(e) => void submit(e)} aria-busy={busy}>
      <h2>{t("household.create.title")}</h2>
      <div className="field">
        <label htmlFor="new-household-name">{t("household.create.name")}</label>
        <input id="new-household-name" type="text" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
      </div>
      {error && (
        <p role="alert" className="form-error">
          {t(error)}
        </p>
      )}
      <button type="submit" className="button" disabled={busy || !name.trim()}>
        {t("household.create.button")}
      </button>
    </form>
  );
}
