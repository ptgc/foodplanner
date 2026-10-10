# Domain

## Glossary
- **Household**: tenant. Owns all data except global catalog data.
- **User**: login identity (external provider or local). **Membership**: User in a Household with a role.
- **Member profile**: a person in a household; kid profiles have no credentials and are selected on a logged-in device.
- **Staple**: ingredient flagged `IsStaple` (salt, oil, flour). Never auto-added to the list; shown in a "check you have these" section.
- **Pantry item**: household memory of confirmed staples.

## Roles (per membership)
Owner: manage members, delete household. Adult: edit recipes/plans, order. Child: pick meals, check off items; no recipe edits, no orders.

## Core entities
Household, User, Membership, MemberProfile, Recipe, RecipeStep, RecipeIngredient(quantity, unit, ingredientId, note), Ingredient(IsStaple), Unit, Tag/Category, MealPlan, MealPlanEntry(date, recipe, servings, pickedBy), ShoppingList, ShoppingListItem(ingredient, mergedQuantity, unit, checked, sources), PantryItem, IngredientProductMapping(household, ingredient -> provider SKU).

## Tenancy
Shared DB/schema. `HouseholdId` on every household-owned row. Global catalog rows (ingredients, seed recipes) have `HouseholdId = null`, read-only to households; households may create their own. Enforced by EF global query filter + isolation tests.

## Invariants
- Merging only combines compatible units (mass with mass, volume with volume); incompatible stay separate lines.
- Scaling recipe servings scales all ingredient quantities proportionally.
- A Child profile can never perform Adult-only actions, regardless of device login.
