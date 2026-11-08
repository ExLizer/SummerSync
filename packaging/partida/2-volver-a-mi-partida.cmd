@echo off
rem Gives this player their own save back from the copy made by the first script.
rem SUMMERSYNC_SAVE_DIR overrides the folder of the game's save; it exists for testing this script.
rem
rem There are no parenthesised blocks around anything that expands a path: a ")" in a folder name would
rem end the block early.
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul

set "SAVE=%USERPROFILE%\AppData\LocalLow\Amistech\My Summer Car"
if defined SUMMERSYNC_SAVE_DIR set "SAVE=%SUMMERSYNC_SAVE_DIR%"
set "BACKUP=%SAVE% - mi partida SummerSync"

echo.
echo  SummerSync - volver a tu partida
echo  --------------------------------
echo  Carpeta de la partida: "%SAVE%"
echo.

tasklist /FI "IMAGENAME eq mysummercar.exe" 2>nul | find /I "mysummercar.exe" >nul
if not errorlevel 1 goto :gameopen
if not exist "%BACKUP%\" goto :nobackup

echo  Recupero tu partida desde:
echo    "%BACKUP%"

rem What the host's save put there and ours did not have must go, or the two would mix.
if exist "%SAVE%\defaultES2File.txt" del "%SAVE%\defaultES2File.txt"
if exist "%SAVE%\items.txt" del "%SAVE%\items.txt"
if exist "%SAVE%\meshsave.txt" del "%SAVE%\meshsave.txt"

rem An empty copy means there was no save to begin with.
dir /B /A-D "%BACKUP%" 2>nul | findstr "^" >nul
if errorlevel 1 goto :restored
copy /Y "%BACKUP%\*.*" "%SAVE%\" >nul
if errorlevel 1 goto :restorefail

:restored
rmdir /S /Q "%BACKUP%"
echo.
echo  Listo: tu partida está de vuelta.
echo  Si Steam avisa de un conflicto con la nube, elegí los archivos LOCALES.
echo.
pause
exit /b 0

:gameopen
echo  El juego está abierto. Cerralo y volvé a ejecutar este archivo.
goto :fail

:nobackup
echo  No hay ninguna copia de tu partida guardada:
echo    "%BACKUP%"
echo  O ya la recuperaste, o nunca ejecutaste "1-usar-la-partida-de-mi-amigo.cmd".
echo  No cambio nada.
goto :fail

:restorefail
echo.
echo  No pude copiar los archivos. Tu copia sigue intacta en la carpeta de arriba.

:fail
echo.
pause
exit /b 1
