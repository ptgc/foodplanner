import type { odaEn } from "./oda-en";

/** Norwegian (bokmål) messages of the Oda product picker and the Oda choices page (T-052). Same keys as oda-en. */
export const odaNb: Record<keyof typeof odaEn, string> = {
  "oda.pick.open": "Velg et annet produkt",
  "oda.pick.listLabel": "Produkter for {name}",
  "oda.pick.inCart": "I handlekurven",
  "oda.pick.use": "Bruk denne neste gang",
  "oda.pick.useFor": "Bruk denne neste gang: {product}",
  "oda.pick.saved": "Lagret til neste gang",
  "oda.pick.remove": "Fjern valget",
  "oda.pick.removeFor": "Fjern valget: {name}",
  "oda.pick.note": "Dette endrer ikke Oda-handlekurven din: bytt produkter i Oda. Vi lagrer bare hvilket produkt denne varen skal bruke, ikke Oda-innloggingen din.",
  "oda.pick.offline": "Koble til nettet for å endre valgene dine.",
  "oda.pick.problem.limit": "Du har like mange Oda-valg som kan lagres. Fjern noen først.",
  "oda.pick.problem.conflict": "Noen endret dette valget samtidig. Prøv igjen.",
  "oda.choices.title": "Dine Oda-valg",
  "oda.choices.intro": "Produktene du har valgt for en vare. Hvilket produkt det er, vises neste gang handlekurven fylles, der det er merket «Ditt Oda-valg».",
  "oda.choices.loading": "Laster valgene dine…",
  "oda.choices.empty": "Ingen valg ennå. Etter «Legg i Oda-handlekurv» åpner du «Velg et annet produkt» på en linje og trykker «Bruk denne neste gang».",
  "oda.choices.setOn": "satt {date}",
  "oda.choices.clear": "Fjern",
  "oda.choices.clearFor": "Fjern valget for {name}",
  "oda.choices.cleared": "Valget er fjernet",
  "oda.choices.offline": "Du er frakoblet. Dette er det som ble lastet sist; koble til for å fjerne et valg.",
  "oda.choices.adultsOnly": "Bare voksne og eiere kan se og endre Oda-valgene.",
};
