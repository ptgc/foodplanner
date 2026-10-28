import type { components } from "../api/schema";
import type { MessageKey } from "../i18n";

export type StoreSection = components["schemas"]["StoreSection"];

/** The sections in the default walking order (Q-019). The server sorts the list; this order is for pickers. */
export const STORE_SECTIONS: readonly StoreSection[] = ["FruitAndVegetables", "MeatFishAndCheese", "Frozen", "Dairy", "DryGoods", "Other"];

export const storeSectionKey = (section: StoreSection): MessageKey => `storeSection.${section}` as MessageKey;

/** Splits already sorted items into runs of the same store section, keeping the server's order. */
export function groupByStoreSection<T extends { storeSection: StoreSection }>(items: T[]): { section: StoreSection; items: T[] }[] {
  const groups: { section: StoreSection; items: T[] }[] = [];
  for (const item of items) {
    const section = item.storeSection ?? "Other"; // a payload without it (older server/app) falls under Other
    const last = groups[groups.length - 1];
    if (last && last.section === section) last.items.push(item);
    else groups.push({ section, items: [item] });
  }
  return groups;
}
