import { useEffect, useRef } from "react";
import { Navigate, Outlet, useLocation } from "react-router";
import { loginPathFor } from "../auth-paths";
import { useSession } from "../session";

export default function Shell() {
  const { session, signOut, setActiveHousehold } = useSession();
  const location = useLocation();
  const mainRef = useRef<HTMLElement>(null);
  const signedIn = session.status === "signedIn";

  // Move focus to the content when the route changes, so keyboard and screen reader users start there.
  useEffect(() => {
    if (signedIn) mainRef.current?.focus({ preventScroll: true });
  }, [location.pathname, signedIn]);

  if (session.status === "loading") return <p role="status">Loading...</p>;
  if (session.status === "signedOut") return <Navigate to={loginPathFor(location)} replace />;

  const active = session.households.find((h) => h.id === session.activeHouseholdId);

  return (
    <div className="shell">
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <header className="shell-header">
        <span className="shell-title">Foodplanner</span>
        <div className="shell-session">
          <span className="shell-email" data-testid="user-email">
            {session.user.email}
          </span>
          {session.households.length > 1 ? (
            <>
              <label htmlFor="household" className="visually-hidden">
                Household
              </label>
              <select
                id="household"
                value={session.activeHouseholdId ?? ""}
                onChange={(e) => setActiveHousehold(e.target.value)}
              >
                {session.households.map((h) => (
                  <option key={h.id} value={h.id}>
                    {h.name}
                  </option>
                ))}
              </select>
            </>
          ) : (
            <span data-testid="household-name">{active?.name ?? "No household"}</span>
          )}
          <button type="button" className="button button-light" onClick={() => void signOut()}>
            Log out
          </button>
        </div>
      </header>
      <main id="main" ref={mainRef} tabIndex={-1} className="shell-main">
        <Outlet />
      </main>
    </div>
  );
}
