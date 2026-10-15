# Popcorn Subtitles för Jellyfin 12.2

Ett testplugin för vanliga film- och avsnittsfiler. Det söker undertexter på dina valda språk via Popcorn Notes när Jellyfin förbereder uppspelningen. Filmer och serier har egna språkval och prioriterade releasegrupper. Träffarna läggs till som externa textspår med `Popcorn` i namnet.

Undertextfilen hämtas först när Jellyfin begär spåret, normalt när du väljer det i undertextmenyn. Servern sparar VTT-filen i sin cache och använder Jellyfins vanliga undertextkod för leverans och konvertering. Befintliga undertexter och videofiler lämnas orörda. Inga undertextposter skrivs till Jellyfins databas.

## Installera

1. Öppna **Kontrollpanel → Pluginer → Förråd (Repositories)** i Jellyfin och lägg till ett förråd med namnet `Popcorn Subtitles` och denna URL:

   ```text
   https://raw.githubusercontent.com/luttman/jellyfin-plugin-popcorn-subtitles/main/manifest.json
   ```

2. Öppna plugin-katalogen och installera **Popcorn Subtitles**.
3. Starta om Jellyfin-servern.
4. Öppna pluginets inställningar. Funktionen är aktiverad och manifestlänken till Popcorn Notes är redan ifylld.
5. Starta en film. Öppna spelarens undertextmeny och välj ett spår med `Popcorn` i namnet.

För manuell installation finns ZIP-filen under [Releases](https://github.com/luttman/jellyfin-plugin-popcorn-subtitles/releases). Skapa mappen `PopcornSubtitles_0.2.0.0` i serverns plugins-mapp och packa upp ZIP-filens innehåll direkt i den mappen. I Jellyfins officiella Docker-installation är plugins-mappen normalt `/config/plugins`. Starta därefter om servern.

Pluginet ska installeras på servern. Inget behöver installeras separat i webbläsaren eller på Android TV. Klienterna använder Jellyfins vanliga undertextspår.

## Språk och releasegrupper

Öppna **Kontrollpanel → Pluginer → Popcorn Subtitles**. Under **Filmer** och **Serier** finns separata inställningar:

- **Språk:** skriv trebokstavskoder i prioritetsordning, exempelvis `swe,eng` för svenska följt av engelska eller `dan,swe,eng` för danska först. Högst 10 språk. Standard är `swe,eng` för båda typerna.
- **Releasegrupper:** skriv gruppnamn i prioritetsordning, exempelvis `FraMeSToR,FGT,RARBG` för filmer och `NTb,FLUX,KOGi` för serier. Fälten är tomma från början; exemplen är inga förval. Högst 30 grupper.

Gruppnamn matchas i tjänstens undertextbeskrivning utan hänsyn till stora och små bokstäver. `NTb` matchar inte `NTbX`. Träffar från prioriterade grupper hamnar först inom varje språk, innan listan begränsas till fyra träffar. Om ingen grupp matchar används tjänstens ordning. Det är en prioritering, inte ett krav som döljer alla andra undertexter. När tjänsten inte anger releasegrupp kan pluginet inte avgöra vilken grupp texten hör till.

Språkvalen ersätter `langs` i tjänstens manifestlänk. Övriga inställningar, som `ai=on`, behålls. Spara och starta om den aktuella uppspelningen för att se nya val. En ändring av språk eller grupper använder en ny sökning och återanvänder inte gamla sökresultat. Uppgradering från 0.1 behåller manifestlänken och aktiveringsvalet; de nya språkfälten får standardvärdet svenska och engelska.

## Förutsättningar och beteende

- Kräver **Jellyfin Server 12.2** och dess .NET 10-miljö. Paketet är kompilerat mot `Jellyfin.Controller 12.2.0`.
- Filmer behöver ett IMDb-id i Jellyfins metadata. Avsnitt behöver seriens IMDb-id samt säsongs- och avsnittsnummer.
- `.strm` och HTTP-strömmar stöds inte i denna version.
- Filens namn och IMDb-id skickas till undertexttjänsten för matchning. Videofilen skickas inte.
- Högst fyra träffar per språk visas, enligt dina språk- och gruppprioriteringar. Synkronisering mot just din filmversion är inte garanterad.
- En sökning väntar högst fem sekunder. Om tjänsten inte svarar fortsätter Jellyfin med de befintliga spåren.
- Sökresultat återanvänds i sex timmar. Hämtade filer ligger under `popcorn-subtitles` i Jellyfins cachemapp. Vid nya nedladdningar rensas filer som inte använts på sju dagar.
- Textspåren tillkommer i uppspelningsinformationen. De behöver inte synas i filmens detaljsida före uppspelning.
- Jellyfins befintliga standardval behålls när spåren läggs till. Spara inte dessa tillfälliga spår som permanenta biblioteksfiler.
- Manifestlänkens `ai=on` och övriga inställningar behålls. Tjänsten kan lägga till exempelvis information, trivia och egna meddelanden i undertexten.

## Verifiering och begränsningar

Verifierat: kompilering mot 12.2, sökning och riktig svensk VTT-nedladdning från Popcorn Notes, samt automatiska kontroller för bland annat samtidig sökning, hämtning först vid val, återanvändning av cache, bevarade befintliga spår, avsnittsidentifiering, hantering av tjänstefel och avvisning av felaktiga nedladdningssvar.

**Inte verifierat i en körande Jellyfin-server, webbläsare eller Android TV.** Det här är en testversion, inte bekräftad klientkompatibilitet. Bränning av text i bilden använder Jellyfins undertextkod men behöver också provas med en riktig server.

Pluginet kompletterar två av Jellyfins servertjänster: mediekällor och undertextkodning. Det kan påverkas av andra pluginer som ersätter samma tjänster, och behöver kontrolleras igen vid serveruppgraderingar.

Om spåren inte visas: kontrollera IMDb-id, att pluginet är aktivt, samt serverloggen efter `Popcorn`. Vid problem kan du stänga av funktionen i pluginets inställningar. För att avinstallera, stoppa servern, ta bort pluginmappen och starta servern igen. Cachefilerna kan tas bort separat ur `popcorn-subtitles` när servern är stoppad.

## Bygga och testa

Kräver .NET SDK 10 och tillgång till NuGet vid första bygget.

```powershell
./build.ps1
dotnet run --project tests/PopcornSubtitles.SelfTest -c Release
node tests/settings-page.test.cjs
```

För att även göra en riktig sökning och hämta en svensk undertext för *The Matrix*:

```powershell
dotnet run --project tests/PopcornSubtitles.SelfTest -c Release -- --live
```

`build.ps1` läser versionen från projektfilen, skapar installationspaketet under `dist` och en `manifest.json` med paketets MD5-kontrollsumma och release-URL. Äldre manifestversioner behålls. Publicera exakt det ZIP-paketet till releasen med motsvarande tagg, exempelvis `v0.2.0.0`, och checka in det genererade manifestet tillsammans med källkoden. Om paketet byggs om ändras kontrollsumman; manifest och publicerat ZIP-paket måste alltid uppdateras tillsammans.

Jellyfins egna bibliotek följer inte med pluginpaketet; de tillhandahålls av servern.
