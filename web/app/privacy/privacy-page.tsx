import { Link } from "react-router";
import { useI18n } from "../i18n";
import { LanguageSwitch } from "../i18n/language-switch";
import { useSession } from "../session";
import "./privacy.css";

/** The privacy page (T-113): what is stored, cookies, the AI import line (Q-028), Oda (ADR 0008) and the user's choices. */
export default function PrivacyPage() {
  const { t } = useI18n();
  const { session } = useSession();
  // Export and delete are screens of the signed-in grown-up app; signed out or in kid mode the text alone is shown.
  const showLinks = session.status === "signedIn" && !session.user.kidMode;
  const backTo = session.status === "signedIn" ? "/" : "/login";

  return (
    <main id="main" className="privacy" aria-labelledby="privacy-title">
      <div className="login-language">
        <LanguageSwitch />
      </div>
      <h1 id="privacy-title">{t("privacy.title")}</h1>
      <p>{t("privacy.intro")}</p>

      <section aria-labelledby="privacy-stores">
        <h2 id="privacy-stores">{t("privacy.stores.title")}</h2>
        <ul>
          <li>{t("privacy.stores.account")}</li>
          <li>{t("privacy.stores.household")}</li>
          <li>{t("privacy.stores.recipes")}</li>
          <li>{t("privacy.stores.plans")}</li>
        </ul>
        <p>{t("privacy.stores.records")}</p>
        <p>{t("privacy.stores.visible")}</p>
        <p>{t("privacy.stores.device")}</p>
        <p>{t("privacy.stores.deviceClear")}</p>
      </section>

      <section aria-labelledby="privacy-cookies">
        <h2 id="privacy-cookies">{t("privacy.cookies.title")}</h2>
        <ul>
          <li>{t("privacy.cookies.signin")}</li>
          <li>{t("privacy.cookies.xsrf")}</li>
          <li>{t("privacy.cookies.oda")}</li>
        </ul>
        <p>{t("privacy.cookies.none")}</p>
      </section>

      <section aria-labelledby="privacy-ai" data-testid="privacy-ai">
        <h2 id="privacy-ai">{t("privacy.ai.title")}</h2>
        <p>{t("privacy.ai.body")}</p>
      </section>

      <section aria-labelledby="privacy-oda" data-testid="privacy-oda">
        <h2 id="privacy-oda">{t("privacy.oda.title")}</h2>
        <p>{t("privacy.oda.body")}</p>
      </section>

      <section aria-labelledby="privacy-rights">
        <h2 id="privacy-rights">{t("privacy.rights.title")}</h2>
        <p>{t("privacy.rights.export")}</p>
        {showLinks && (
          <p>
            <Link className="button" to="/household" data-testid="privacy-export-link">
              {t("privacy.rights.exportLink")}
            </Link>
          </p>
        )}
        <p>{t("privacy.rights.delete")}</p>
        <p>{t("privacy.rights.handover")}</p>
        {showLinks && (
          <p>
            <Link className="button" to="/account/delete" data-testid="privacy-delete-link">
              {t("privacy.rights.deleteLink")}
            </Link>
          </p>
        )}
      </section>

      <p>
        <Link className="auth-link" to={backTo}>
          {t("privacy.back")}
        </Link>
      </p>
    </main>
  );
}
