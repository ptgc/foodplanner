import type { privacyEn } from "./privacy-en";

/** Norwegian bokmål messages of the privacy page (T-113), the counterpart of privacy-en.ts. Wording awaits the architect. */
export const privacyNb: Record<keyof typeof privacyEn, string> = {
  "privacy.title": "Personvern",
  "privacy.back": "Tilbake til appen",
  "privacy.intro": "Foodplanner er en familieapp for å planlegge måltider. Her står det med enkle ord hva appen lagrer og hvem som kan se det.",
  "privacy.stores.title": "Hva Foodplanner lagrer",
  "privacy.stores.account": "Kontoen din: navn, e-postadresse og det som trengs for å logge deg inn (passordet lagres bare som en enveis-hash).",
  "privacy.stores.household": "Husholdningen din: navn, hvem som er medlem og rollen til hvert medlem (eier, voksen eller barn), invitasjoner, og barneprofiler med valgfri PIN (lagret som hash).",
  "privacy.stores.recipes": "Oppskrifter med ingredienser, trinn og merkelapper, og oppskriftsbilder. Bildene gjøres mindre og kameradata fjernes. Likes og teksten fra uferdige oppskriftsimporter lagres også.",
  "privacy.stores.plans": "Ukeplaner, handlelister, skapet, faste varer og butikker med rekkefølgen på avdelingene.",
  "privacy.stores.visible": "Alt dette deles med medlemmene i husholdningen det hører til, og med ingen utenfor.",
  "privacy.stores.device": "På enheten din lagrer appen språket ditt, valgt husholdning og en kopi av økten og listene, slik at den kan starte og virke uten nett. Når du logger ut eller sletter kontoen, fjernes dette.",
  "privacy.cookies.title": "Informasjonskapsler",
  "privacy.cookies.signin": "En innloggingskapsel holder deg innlogget. Den trengs for at appen skal virke, varer opptil 14 dager i bruk og kan ikke leses av skript.",
  "privacy.cookies.oda": "En kortvarig, kryptert kapsel settes mens du legger handlelisten i Oda. Den varer i høyst 10 minutter og fjernes når du kommer tilbake.",
  "privacy.cookies.none": "Det finnes ingen reklame- eller sporingskapsler.",
  "privacy.ai.title": "KI-hjelp ved import av oppskrifter",
  "privacy.ai.body": "Hvis KI-hjelp er slått på, kan linjer vi ikke klarer å lese, bli sendt til Anthropic (uten navn eller kontodata). Bare de uleste ingredienslinjene i en import sendes, aldri hele siden, og ingenting annet om deg eller husholdningen. Du sjekker alltid resultatet før det lagres.",
  "privacy.oda.title": "Oda",
  "privacy.oda.body": "«Legg i Oda-handlekurv» logger deg inn hos Oda bare for den ene handlingen. Foodplanner lagrer ingen Oda-innloggingsnøkkel og ingen data fra Oda. Varene på handlelisten sendes til Oda for å fylle handlekurven, og forbindelsen lukkes etterpå.",
  "privacy.rights.title": "Dine valg",
  "privacy.rights.export": "Eiere og voksne kan eksportere alt en husholdning har lagret, som en fil, fra husholdningssiden.",
  "privacy.rights.delete": "Du kan slette kontoen din når som helst. Husholdninger der du er eneste medlem, slettes sammen med den.",
  "privacy.rights.exportLink": "Åpne husholdningssiden",
  "privacy.rights.deleteLink": "Slett kontoen min",
};
