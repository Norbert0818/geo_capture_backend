@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem A BAT helyetol felfele keressuk a backend gyokeret.
for %%I in ("%~dp0.") do set "geoRoot=%%~fI"

:findRoot
if exist "%geoRoot%\GeoCapture.Api\GeoCapture.Api.csproj" goto rootFound
for %%I in ("%geoRoot%\..") do set "geoParent=%%~fI"
if /I "%geoParent%"=="%geoRoot%" goto rootMissing
set "geoRoot=%geoParent%"
goto findRoot

:rootFound
set "geoApiProject=%geoRoot%\GeoCapture.Api\GeoCapture.Api.csproj"
set "geoTestProject=%geoRoot%\GeoCapture.Api.Tests\GeoCapture.Api.Tests.csproj"
if not exist "%geoTestProject%" goto testsMissing

pushd "%geoRoot%"
if errorlevel 1 goto failedBeforeStart
echo Backend gyokere: "%geoRoot%"
echo Backend-tesztek es munkalap-migracio...
dotnet test "%geoTestProject%"
if errorlevel 1 goto failed
dotnet run --project "%geoApiProject%" --no-build --no-launch-profile -- migrate-field-work
if errorlevel 1 goto failed
popd
echo KESZ. Inditsd ujra a helyi backendet a korabbi inditoval.
pause
exit /b 0

:rootMissing
echo HIBA: A backend-projekt nem talalhato a BAT mappajaban vagy folotte.
echo Tedd a BAT-ot a backend gyokerebe vagy a GeoCapture.Api mappaba.
echo Keresett fajl: GeoCapture.Api\GeoCapture.Api.csproj
goto failedBeforeStart

:testsMissing
echo HIBA: Hianyzik a tesztprojekt:
echo "%geoTestProject%"
echo A GeoCapture.Api.Tests mappa a GeoCapture.Api mappa mellett legyen.
echo A tesztprojekt CSProj fajlja es a korabbi tesztfajlok is szuksegesek.
goto failedBeforeStart

:failed
popd
:failedBeforeStart
echo HIBA: A frissites nem sikerult. A tovabbi lepesek nem futottak le.
pause
exit /b 1