@echo off
rem Конвейер видео TOKENFALL (Windows).
rem   run.cmd                 - все сцены из conveer.json
rem   run.cmd boss_* death    - выбранные сцены (имя или шаблон с *)
rem   run.cmd list            - список сцен
rem   run.cmd --quick cell_01 - быстро и в низком качестве
rem Папка игры берётся из "gameRoot" в conveer.json (по умолчанию ..\game-token - рядом с конвейером).
setlocal
chcp 65001 >nul
cd /d "%~dp0"
set "GAMEROOT=../game-token"
for /f "usebackq delims=" %%g in (`powershell -NoProfile -Command "(Get-Content -Raw -Encoding UTF8 conveer.json | ConvertFrom-Json).gameRoot"`) do set "GAMEROOT=%%g"
set "GAMEROOT=%GAMEROOT:/=\%"
pushd "%GAMEROOT%" 2>nul || (echo Не найдена игра: %GAMEROOT% - положите game-token рядом с conveer-video или поправьте gameRoot в conveer.json & exit /b 2)
set "GAMEDIR=%CD%"
popd
dotnet build -c Release -nologo -v q -p:GameRoot="%GAMEDIR%" || exit /b 1
dotnet bin\Release\net8.0\Conveer.dll %*
