# Flow and usability review, November 2026 (T-134)

Walked on 2026-11-25 by Claude on the local stack (http://localhost:3000) at 390 x 844, signed in as the local admin of "Home", in Norwegian. The household has 37 recipes and nothing planned this week. Nothing was changed: no meals planned, no items added. Screenshots are in [flow-review-2026-11/](flow-review-2026-11/). Screenshots with cookbook photos are left out because the photos are copyrighted.

This review is about flow and layout only. Colours, fonts and visual style belong to T-136.

## Findings per screen

### Every screen: the header and menu ([01-home.jpg](flow-review-2026-11/01-home.jpg))
- The green header takes about 140 px of the 844 px screen. On top of that, the tab row takes about 50 px, so about a quarter of every screen is chrome. The header shows the email, the household name, a language picker, "Bytt profil" and "Logg ut". These are things you use rarely, but they are always visible.
- The tab row is at the top (hard to reach with a thumb), and "Husstand" touches the right edge. The row has no room for more tabs, so Pantry and "Hva kan jeg lage?" have no tab at all.
- "Bytt profil" opens an empty sheet ("Ingen barneprofiler ennå") when the household has no kid profiles.

### Start screen ([01-home.jpg](flow-review-2026-11/01-home.jpg))
- After signing in you land on "API-status / API-et er i orden / Database: Oppe". This is a developer page. It doesn't say what to do next or what's for dinner today.

### Recipes ([02-recipes.jpg](flow-review-2026-11/02-recipes.jpg))
- Three buttons to add a recipe ("Importer oppskrift", "Fra bilde", "Ny oppskrift") come before the list.
- About 25 filter chips (time, protein, meal type, tags) fill the whole first screen. You must scroll past all of them to see the first recipe.
- Many recipes have no picture. They get a letter placeholder or a grey box while the image loads, so the list doesn't feel image-led.
- "Vis flere" paging works.

### Recipe detail
- The detail page has no way to put the recipe on the week plan. You can only do that from the plan, by tapping + on a day.
- The order is: "Alle oppskrifter", "Start matlagingen", "Rediger", a large picture, the title, then the tip and the source. Ingredients start below the first screen.

### Week plan ([03-week-plan.jpg](flow-review-2026-11/03-week-plan.jpg))
- This is clear and works well: the week switcher, seven day cards with "+", and today marked.
- An empty week offers no shortcut. There is no "fill the week for me" or "same as last week", so planning means seven separate picks.
- The dinner picker is image-led and good. But its filter chips are Bakst, Dessert, Frokost, Lunsj and Middag, which are meal types, while you are picking dinner. Protein, time or "barnevennlig" would be more useful there.
- "Lås uka" sits above the days with no explanation of what it does.
- "Lag handleliste" is at the very bottom, after Sunday.

### Shopping list ([04-shopping.jpg](flow-review-2026-11/04-shopping.jpg))
- Nine controls come before the list itself:
  - the week switcher
  - "Lag fra ukemenyen"
  - "Legg til faste varer" (disabled) with a hint
  - "Del" and "Kopier"
  - "Legg i Oda-handlekurv" (disabled) with a hint
  - "Dine Oda-valg"
  - "Det vi har hjemme"
- "Lag fra ukemenyen" is enabled even when the week has no meals.
- With an empty list, the Oda hint says "Ingenting å sende til Oda: alt er krysset av". That is wrong: nothing is ticked, the list is empty.
- The "Legg til en vare" form is at the bottom of the page. On a long list it is far away.
- "Dine Oda-valg" (and the coming "Synk med Oda", T-130) is a separate button with no context.

### Oda choices
- The empty state explains the 3-step way to create a choice, one item at a time. T-130's "Synk med Oda" will be the shortcut, so the empty state should point to it.

### Pantry ("Skapet") and "Hva kan jeg lage?"
- You reach them only through buttons on the shopping list and the household page, not from the menu.
- "Hva kan jeg lage?" with an empty pantry still suggests recipes "missing 1-2 things", for example 5 eggs and 600 ml milk. That is useful, but the screen doesn't say the pantry is empty or how to fill it. T-132 (pantry from a photo) fits here.

### Household ([05-household.jpg](flow-review-2026-11/05-household.jpg))
- One very long page with 15 sections:
  - name and role
  - members
  - invitations
  - ingredients
  - kid profiles
  - PIN
  - weekly dinner mix (9 rule rows)
  - regular items
  - shops (with an aisle order)
  - pantry link
  - export
  - leave household
  - delete household
  - create a new household
  - delete account
  - privacy
- Everyday settings and dangerous actions sit on the same scrolling page, with nothing to jump to.

### Cooking ([06-cook.jpg](flow-review-2026-11/06-cook.jpg))
- The step view itself is good: large text, step counter, progress bar, large next and previous buttons, and swiping.
- The full app header and menu stay visible, taking a quarter of the screen while you cook.
- Ingredients are behind "Vis ingredienser" below the buttons.
- The yellow message "Kunne ikke holde skjermen på" takes space on every step. (It may be specific to the desktop app's browser; check on a phone.)

### Sign in and sign up
- These are simple and clear.
- Sign-up sends an email link. Locally that email can't be read, so on the local stack you can only sign in with the seeded accounts. This is a dev-only issue.

## Proposals, ranked by value against effort

| # | Proposal | Value | Effort | Notes |
|---|---|---|---|---|
| P1 | A real start screen, "I dag". It shows today's dinner (or "plan tonight"), the week at a glance, the shopping list status, and shortcuts ("Hva kan jeg lage?", "Fra bilde"). It replaces API-status. | High | S-M | Merge with T-131: the "Kom i gang" checklist lives here until it is done. |
| P2 | A lighter app frame. Move the email, household, language, "Bytt profil" and "Logg ut" into a profile button or sheet. Put a bottom tab bar within thumb reach: I dag, Ukeplan, Oppskrifter, Handleliste, Mer. "Mer" holds Skapet, Husstand, Hjelp and the rest. | High | M | Gives about 150 px back on every screen. Do it first: every later change builds on it. |
| P3 | Recipes list. One "+ Legg til" button that opens a choice: Fra bilde, Fra nettadresse or tekst, or Skriv selv. Filters go behind a "Filter" button that opens a sheet, with a short row of quick chips (Barnevennlig, Maks 30 min, Fisk, Vegetar). Recipes then show on the first screen. | High | S-M | |
| P4 | Recipe detail. Add "Legg til i ukeplanen" (pick a day). Order the page as picture, title, time and portions, then ingredients, then steps. Move "Rediger" into a "..." menu and make "Start matlagingen" the main button. | High | S | |
| P5 | Week plan. An empty week offers "Fyll uka for meg" (suggest 7 dinners using the dinner-mix rules and kid likes) and "Kopier forrige uke". The picker filters on protein, time and barnevennlig. "Lag handleliste" becomes a sticky button once something is planned. "Lås uka" gets a short explanation. | High | M-L | The "fill the week" suggestion is new logic: it needs its own spec (and Domain code). |
| P6 | Shopping list. The list comes first, with an "Legg til vare" field at the top. Rarely used actions (Del, Kopier, Faste varer) go into a "..." menu. The Oda block is one card: "Legg i Oda-handlekurv", "Dine Oda-valg" and "Synk med Oda". "Lag fra ukemenyen" shows only when the week has meals. Fix the wrong "alt er krysset av" message on an empty list. | High | M | The message fix is a small bug and can go first on its own. |
| P7 | Household split into sub-pages with a menu: Medlemmer og invitasjoner · Barn og PIN · Ukens middagsmiks · Faste varer · Butikker · Ingredienser · Skapet · Data og konto (export, leave, delete, delete account). | Med-High | M | Routes only; the content stays the same. |
| P8 | Cooking full screen. Hide the header and menu, put ingredients in a pull-up sheet, and show the screen-lock message once, small. | Medium | S-M | |
| P9 | Pantry and "Hva kan jeg lage?" become reachable from "I dag" and "Mer". "Hva kan jeg lage?" says when the pantry is empty and links to filling it (T-132). | Medium | S | Comes with P1 and P2. |
| P10 | Hide "Bytt profil" until the household has a kid profile. When none exists, the sheet links straight to setting one up. | Low-Med | S | |
| P11 | Recipes without a picture. The card offers "Legg til bilde" (camera or gallery). Load the grey placeholders in a softer way. | Medium | S-M | Part of the look belongs to T-136. |

## Recommended order
1. **P2 + P1 (+ T-131):** the new frame and the "I dag" start screen, with Kom i gang on it. Everything else is placed inside this frame. Rewrite T-131 to build on P1.
2. **P6's wrong-message bug:** a small fix, any time.
3. **P3 + P4:** recipes list and detail.
4. **P6:** shopping list and the Oda card. This comes after T-130 lands, so "Synk med Oda" gets its place.
5. **P7, then P8, P9, P10, P11.**
6. **P5's "fill the week"** after a spec, since it is new logic.
7. **T-136 (the look)** after steps 1-4, so the new style is applied to the new layout.

## Not covered
- Kid mode itself (no kid profile exists in "Home").
- The invite and accept flow (needs a second account).
- Import review and photo import end to end (they cost Claude calls and create data).
- English text.
- A real phone: the wake lock, the camera, and the keyboard covering inputs.
