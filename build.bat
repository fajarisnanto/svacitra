@echo off
rem svacitra - build, test and publish the single-file Windows exe.
rem Requires the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0).
setlocal
cd /d "%~dp0"

echo === Running unit tests ===
dotnet test PicasaRevive.sln -c Release --nologo
if errorlevel 1 (
  echo.
  echo Tests failed or could not run. If the message mentions "Application Control policy",
  echo Windows Smart App Control is blocking unsigned local builds - see README.md.
  exit /b 1
)

echo.
echo === Publishing svacitra.exe ===
dotnet publish PicasaRevive.App\PicasaRevive.App.csproj -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish --nologo
if errorlevel 1 exit /b 1

echo.
echo Done: %~dp0publish\svacitra.exe
endlocal
