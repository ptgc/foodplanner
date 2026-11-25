import { Link } from "react-router";
import { useI18n, type MessageKey } from "../i18n";
import { useSession } from "../session";

const screens: [string, MessageKey][] = [
  ["/pantry", "pantry.title"],
  ["/what-can-i-make", "nav.suggest"],
  ["/household", "nav.household"],
  ["/ingredients", "recipes.ingredients"],
  ["/privacy", "privacy.link"],
];

/** "Mer" (T-137): every screen that has no tab in the bottom bar (Support for a system admin). T-131 adds Hjelp here. */
export default function More() {
  const { t } = useI18n();
  const { session } = useSession();
  const items = session.status === "signedIn" && session.user.isSystemAdmin ? [...screens, ["/support", "nav.support"] as [string, MessageKey]] : screens;

  return (
    <section aria-labelledby="more-heading">
      <h1 id="more-heading">{t("nav.more")}</h1>
      <ul className="more-list">
        {items.map(([to, label]) => (
          <li key={to}>
            <Link to={to}>{t(label)}</Link>
          </li>
        ))}
      </ul>
    </section>
  );
}
