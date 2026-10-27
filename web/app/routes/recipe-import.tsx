import { Link, useNavigate, useSearchParams } from "react-router";
import { useI18n } from "../i18n";
import { ImportReview } from "../imports/import-review";
import { ImportStart, OpenDrafts } from "../imports/import-start";
import { canEditRecipes, useActiveRole } from "../recipes/use-catalog";
import { useSession } from "../session";

/**
 * `/recipes/import` (Adult or Owner): step 1 paste a web address or text, step 2 (`?draft=<id>`) review the parsed
 * recipe, fix ingredient lines that did not match, and save. A Child sees a message; the API refuses them (403) anyway.
 */
export default function RecipeImport() {
  const { t } = useI18n();
  const { session } = useSession();
  const role = useActiveRole();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  if (session.status !== "signedIn") return null;
  if (!session.activeHouseholdId) return <p>{t("recipes.noHousehold")}</p>;
  const householdId = session.activeHouseholdId;
  const draftId = params.get("draft");

  if (!canEditRecipes(role)) {
    return (
      <section className="recipe-edit">
        <Link to="/recipes" className="back-link">
          {t("recipes.back")}
        </Link>
        <p role="alert" data-testid="no-access">
          {t("import.noAccess")}
        </p>
      </section>
    );
  }

  return (
    <section className="recipe-edit import" aria-labelledby="import-title">
      <Link to="/recipes" className="back-link">
        {t("recipes.back")}
      </Link>
      <h1 id="import-title">{draftId ? t("import.reviewTitle") : t("import.title")}</h1>
      {draftId ? (
        <ImportReview key={`${householdId}/${draftId}`} householdId={householdId} draftId={draftId} />
      ) : (
        <>
          <ImportStart key={householdId} householdId={householdId} onCreated={(draft) => void navigate(`/recipes/import?draft=${draft.id}`)} />
          <OpenDrafts key={`drafts-${householdId}`} householdId={householdId} />
        </>
      )}
    </section>
  );
}
