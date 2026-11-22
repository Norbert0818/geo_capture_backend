@echo off
setlocal
pushd "%~dp0"
if not exist "GeoCapture.Api.csproj" goto wrongfolder

echo Helyi .NET eszkozok visszaallitasa...
dotnet tool restore
if errorlevel 1 goto failed

echo Tesztek futtatasa...
dotnet test "..\GeoCapture.Api.Tests\GeoCapture.Api.Tests.csproj"
if errorlevel 1 goto failed

if exist "Data\Migrations\*_AddSerialNumberPair.cs" goto update
echo A ket szeriaszam migraciojanak letrehozasa...
dotnet ef migrations add AddSerialNumberPair --context AppDbContext --output-dir Data\Migrations
if errorlevel 1 goto failed

:update
echo Adatbazis frissitese...
dotnet ef database update --context AppDbContext
if errorlevel 1 goto failed
echo Kesz. Inditsd ujra a backendet.
popd
pause
exit /b 0

:wrongfolder
echo HIBA: Ezt a fajlt a GeoCapture.Api.csproj melle masold.
goto failed

:failed
echo HIBA: A fenti lepes nem sikerult. A tovabbi lepesek nem futottak le.
popd
pause
exit /b 1