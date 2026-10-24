import { index, layout, route, type RouteConfig } from "@react-router/dev/routes";

export default [
  route("login", "routes/login.tsx"),
  layout("routes/shell.tsx", [
    index("routes/home.tsx"),
    route("recipes", "routes/recipes.tsx"),
    route("recipes/:id", "routes/recipe-detail.tsx"),
    route("shopping", "routes/shopping.tsx"),
  ]),
] satisfies RouteConfig;
