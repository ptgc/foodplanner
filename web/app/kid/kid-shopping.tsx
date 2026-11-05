import { useI18n } from "../i18n";
import type { HubLike } from "../shopping/realtime";
import { useShoppingList } from "../shopping/use-shopping-list";
import { mondayOf } from "../shopping/week";
import type { components } from "../api/schema";
import { CartIcon, CheckIcon } from "./icons";

type StoreSection = components["schemas"]["StoreSection"];

// The catalog has no pictures of ingredients, so every tile shows the picture of its shop section.
const SECTION_PICTURE: Record<StoreSection, string> = {
  FruitAndVegetables: "\u{1F966}",
  MeatFishAndCheese: "\u{1F969}",
  Frozen: "\u{2744}\u{FE0F}",
  Dairy: "\u{1F95B}",
  DryGoods: "\u{1F33E}",
  Other: "\u{1F6D2}",
};

/**
 * The kid's view of the shopping list (T-105): look only. Big tiles with a picture and the name, checked items faded with a
 * check mark. Nothing here writes: the hook runs read only (no check-off queue, no pantry), and there is no control that could.
 * Same live updates and offline copy as the grown-up list.
 */
export function KidShopping({ householdId, hubFactory }: { householdId: string; hubFactory?: () => HubLike }) {
  const { t } = useI18n();
  const model = useShoppingList(householdId, mondayOf(new Date()), { readOnly: true, hubFactory });
  const items = model.list?.main ?? [];

  return (
    <section className="kid-shop" aria-label={t("kid.shopping.title")} data-testid="kid-shopping">
      {model.loading && <p role="status">{t("kid.shopping.loading")}</p>}
      {(model.loadError || model.forbidden) && !model.list && (
        <p role="status" className="kid-shop-empty">
          <span className="kid-shop-empty-icon" aria-hidden="true">
            <CartIcon />
          </span>
          {t("kid.shopping.error")}
        </p>
      )}
      {model.list && items.length === 0 && (
        <p className="kid-shop-empty" data-testid="kid-shopping-empty">
          <span className="kid-shop-empty-icon" aria-hidden="true">
            <CartIcon />
          </span>
          {t("kid.shopping.empty")}
        </p>
      )}
      {items.length > 0 && (
        <ul className="kid-shop-tiles">
          {items.map((item) => (
            <li key={item.id} className={item.checked ? "kid-shop-tile kid-shop-done" : "kid-shop-tile"} data-testid="kid-shop-tile">
              <span className="kid-shop-picture" aria-hidden="true">
                {SECTION_PICTURE[item.storeSection] ?? SECTION_PICTURE.Other}
              </span>
              <span className="kid-shop-name">{item.name}</span>
              {item.checked && (
                <span className="kid-shop-check" data-testid="kid-shop-check">
                  <CheckIcon />
                  <span className="visually-hidden">{t("kid.shopping.done")}</span>
                </span>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
