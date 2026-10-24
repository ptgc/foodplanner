import { index, layout, route, type RouteConfig } from "@react-router/dev/routes";

export default [
  route("login", "routes/login.tsx"),
  layout("routes/shell.tsx", [
    index("routes/home.tsx"),
    route("recipes", "routes/recipes.tsx"),
    route("recipes/new", "routes/recipe-edit.tsx", { id: "recipe-new" }),
    route("recipes/:id", "routes/recipe-detail.tsx"),
    route("plan", "routes/plan.tsx"),
    route("recipes/:id/edit", "routes/recipe-edit.tsx", { id: "recipe-edit" }),
    route("shopping", "routes/shopping.tsx"),
  ]),
] satisfies RouteConfig;
