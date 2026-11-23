import { index, layout, route, type RouteConfig } from "@react-router/dev/routes";

export default [
  route("login", "routes/login.tsx"),
  route("register", "routes/register.tsx"),
  route("set-password", "routes/set-password.tsx"),
  route("privacy", "routes/privacy.tsx"),
  layout("routes/shell.tsx", [
    index("routes/home.tsx"),
    route("recipes", "routes/recipes.tsx"),
    route("recipes/new", "routes/recipe-edit.tsx", { id: "recipe-new" }),
    route("recipes/import", "routes/recipe-import.tsx"),
    route("recipes/import/photo", "routes/recipe-import-photo.tsx"),
    route("recipes/:id","routes/recipe-detail.tsx"),
    route("recipes/:id/cook", "routes/recipe-cook.tsx"),
    route("plan", "routes/plan.tsx"),
    route("recipes/:id/edit", "routes/recipe-edit.tsx", { id: "recipe-edit" }),
    route("shopping", "routes/shopping.tsx"),
    route("shopping/oda-choices", "routes/oda-choices.tsx"),
    route("oda/callback", "routes/oda-callback.tsx"),
    route("pantry", "routes/pantry.tsx"),
    route("what-can-i-make", "routes/what-can-i-make.tsx"),
    route("household", "routes/household.tsx"),
    route("support", "routes/support.tsx"),
    route("account/delete", "routes/account-delete.tsx"),
    route("ingredients", "routes/ingredients.tsx"),
    route("invite/:token", "routes/invite.tsx"),
  ]),
] satisfies RouteConfig;
