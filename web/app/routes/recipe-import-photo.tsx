import { Suspense } from "react";
import { Link, useNavigate } from "react-router";
import { useI18n } from "../i18n";
import { lazyWithMessages } from "../i18n/lazy";
import { canEditRecipes, useActiveRole } from "../recipes/use-catalog";
import { useSession } from "../session";

// The screen, its picker and its texts (message group "photo") arrive together; see photo/photo-import-screen.tsx.
const Screen = lazyWithMessages("photo", () => import("../photo/photo-import-screen"));

/** `/recipes/import/photo` (Adult or Owner): add a recipe from photos, then continue in the import review. */
export default function RecipeImportPhoto() {
  const { t } = useI18n();
  const { session } = useSession();
  const role = useActiveRole();
  const navigate = useNavigate();
  if (session.status !== "signedIn") return null;
  if (!session.activeHouseholdId) return <p>{t("recipes.noHousehold")}</p>;
  const householdId = session.activeHouseholdId;

  return (
    <div className="recipe-edit import">
      <Link to="/recipes" className="back-link">
        {t("recipes.back")}
      </Link>
      {canEditRecipes(role) ? (
        <Suspense fallback={<p role="status">{t("common.loading")}</p>}>
          <Screen key={householdId} householdId={householdId} onCreated={(draft) => void navigate(`/recipes/import?draft=${draft.id}`)} />
        </Suspense>
      ) : (
        <p role="alert" data-testid="no-access">
          {t("import.noAccess")}
        </p>
      )}
    </div>
  );
}
