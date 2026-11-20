# GeoCapture – webes frissítés és indítás dupla kattintással

A három indítófájlt másold a Flutter projekt gyökerébe: ugyanoda, ahol a `pubspec.yaml` és a `lib` mappa van.

| Fájl | Mire való? |
| --- | --- |
| `GeoCapture_inditas.bat` | Fordít, átmásolja a webes fájlokat, elindítja a backendet és megnyitja a böngészőt. |
| `geocapture_dev.ps1` | Az indító által használt PowerShell-kód; maradjon a BAT mellett. |
| `GeoCapture_backend_valasztas.bat` | Ezzel később másik backendmappát választhatsz ki. |

## Használat

1. Csomagold ki a ZIP-et, és a fenti három fájlt másold közvetlenül a Flutter projekt mappájába. A ZIP-en belül nincs új alkalmazásprojekt.
2. Kattints duplán a `GeoCapture_inditas.bat` fájlra.
3. Ha a Flutter projekt mellett van a `geo_capture_backend/GeoCapture.Api` mappa, az indító megkeresi. A `GeoCapture.Backend/GeoCapture.Api`, a külön `GeoCapture.Api` és a Flutter projekten belüli `backend/GeoCapture.Api` elrendezést is ismeri.
4. Ha nem talál egyértelműen egyetlen backendet, mappaválasztót nyit. Válaszd ki a saját backend megoldásod mappáját vagy közvetlenül a `GeoCapture.Api` mappát. A projektfájl neve `GeoCapture.Api.csproj`, mellette pedig `Program.cs` legyen.
5. Megjegyzi a választást, majd elkészíti a webes buildet és átmásolja a `wwwroot` mappába.
6. Ha a 7043-as port szabad, külön ablakban elindítja a backendet a korábbi `https://localhost:7043` címen. A backend ablakát hagyd nyitva, amíg használod az alkalmazást.

Később minden webes frissítéshez elég a `GeoCapture_inditas.bat` fájlra duplán kattintani. A konzolba nem kell útvonalat vagy parancsot beírnod. Ha már a GeoCapture fut a 7043-as porton, az indító a frissítés után azt használja; a böngészőben Ctrl+F5-tel töltsd újra az oldalt.

Az indítás fordítást és másolást végez; a korábbi `flutter analyze` és `flutter test` ellenőrzéseket fejlesztés közben külön futtasd az IDE-ből vagy terminálból.

## Ha máshol van vagy megváltozik a projekt útvonala

A Flutter mappáját az indító a saját helyéből állapítja meg. Nincs beleírva sem `C:\dev`, sem `E:\Projects`.

A kiválasztott backend útját a `.dart_tool/geocapture/launcher.json` fájlban tartja. Azonos meghajtón relatív elérési utat ment, ezért ha a két projektet együtt mozgatod ugyanabban az elrendezésben, a kapcsolat megmarad. Eltérő meghajtóknál teljes elérési utat ment.

Ha a mentett hely már nem létezik, újra megkeresi vagy kiválasztatja a backendet. Másik, még létező backend kiválasztásához kattints duplán a `GeoCapture_backend_valasztas.bat` fájlra. Ez csak a mappaválasztást menti el.

A `flutter clean` törölheti a `.dart_tool` alatt mentett választást; az indító ilyenkor újra felismeri a közeli projektet, vagy megnyitja a mappaválasztót.

## A mostani Access denied hiba

A korábbi parancsban szereplő `C:\dev\geo_capture_backend\GeoCapture.Api\wwwroot` egy konkrét példaútvonal volt. Az új indító meglévő backend projektet keres, és csak annak a `wwwroot` mappájába másol. A backend szülőmappáit nem próbálja létrehozni egy téves útvonal alapján.

Másolás előtt ellenőrzi az írási hozzáférést. Ha a kiválasztott hely nem írható, másik projektmappát kér. Ha a tényleges projekted is védett helyen van, tedd a teljes backend projektet egy saját, írható fejlesztői mappába, majd válaszd ki ott. A valódi Windows-jogosultsághiányt az elérési út felismerése önmagában nem oldja fel.

Ha egy meglévő fájl nem írható vagy zárolt, a másolás hibáját kiírja, és megáll. A Flutter-fordítás hibájánál nem másolja át a régi buildet.

## A meglévő beállítások

A korábbi `.env`-et továbbra is a backend olvassa. Az indítóba nem kell adatbázis-jelszót másolni. Ha a tanúsítványt vagy a backendet is áthelyezed, a `.env`-ben lévő `Root Certificate` elérési út továbbra is a tényleges tanúsítványra mutasson.

Az indító a telepített Fluttert és .NET-et használja. A `flutter` és `dotnet` parancs legyen elérhető a Windows PATH-ban. A már korábban működő localhost HTTPS-tanúsítványt használja.

Leállítás: Ctrl+C a backend ablakában. Ha más alkalmazás foglalja a 7043-as portot, előbb tedd szabaddá azt; az indító nem állít le más folyamatokat.

## Ellenőrzés állapota

A forrást és a csomag tartalmát ellenőriztem. Ebben a környezetben nincs Windows PowerShell, Flutter vagy .NET, ezért a Windowsos indítást, a mappaválasztót és a tényleges buildet itt nem tudtam futtatni.

## Dokumentáció

- PowerShell saját könyvtára: https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_automatic_variables
- PowerShell indítási paraméterei: https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_powershell_exe
- Robocopy és visszatérési kódjai: https://learn.microsoft.com/windows-server/administration/windows-commands/robocopy
