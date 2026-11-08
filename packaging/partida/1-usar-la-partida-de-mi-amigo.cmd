@echo off
rem Puts the host's save in place of this player's own, after keeping a copy of theirs.
rem SUMMERSYNC_SAVE_DIR overrides the folder of the game's save; it exists for testing this script.
rem
rem There are no parenthesised blocks around anything that expands a path: a ")" in a folder name, as in
rem a user called "Ana (casa)", would end the block early.
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul

set "SAVE=%USERPROFILE%\AppData\LocalLow\Amistech\My Summer Car"
if defined SUMMERSYNC_SAVE_DIR set "SAVE=%SUMMERSYNC_SAVE_DIR%"
set "BACKUP=%SAVE% - mi partida SummerSync"
set "SOURCE=%~dp0"

echo.
echo  SummerSync - usar la partida de tu amigo
echo  ----------------------------------------
echo  Carpeta de la partida: "%SAVE%"
echo.

tasklist /FI "IMAGENAME eq mysummercar.exe" 2>nul | find /I "mysummercar.exe" >nul
if not errorlevel 1 goto :gameopen
if not exist "%SOURCE%defaultES2File.txt" goto :nosource
if not exist "%SAVE%\" mkdir "%SAVE%"
if exist "%BACKUP%\" goto :havebackup

echo  Guardo una copia de TU partida en:
echo    "%BACKUP%"
mkdir "%BACKUP%"
if errorlevel 1 goto :copyfail

rem Someone who has never saved has nothing to keep; an empty copy still marks that this script ran.
dir /B /A-D "%SAVE%" 2>nul | findstr "^" >nul
if errorlevel 1 goto :install
copy /Y "%SAVE%\*.*" "%BACKUP%\" >nul
if errorlevel 1 goto :backupfail
goto :install

:havebackup
echo  Ya hay una copia de tu partida, de una vez anterior:
echo    "%BACKUP%"
echo  No la toco. Solo actualizo la partida de tu amigo.

:install
echo.
echo  Copio la partida de tu amigo...
call :put defaultES2File.txt
if errorlevel 1 goto :copyfail
call :put items.txt
if errorlevel 1 goto :copyfail
call :put meshsave.txt
if errorlevel 1 goto :copyfail

echo.
echo  Listo. Abrí el juego desde Steam.
echo  Si Steam avisa de un conflicto con la nube, elegí los archivos LOCALES.
echo  Para volver a tu partida, ejecutá "2-volver-a-mi-partida.cmd".
echo.
pause
exit /b 0

rem One file of the host's save. If the host's save does not have it, ours must not stay either.
:put
if not exist "%SOURCE%%~1" goto :putnothing
copy /Y "%SOURCE%%~1" "%SAVE%\%~1" >nul
exit /b %errorlevel%
:putnothing
if exist "%SAVE%\%~1" del "%SAVE%\%~1"
exit /b 0

:gameopen
echo  El juego está abierto. Cerralo y volvé a ejecutar este archivo.
goto :fail

:nosource
echo  No encuentro la partida de tu amigo al lado de este archivo.
echo  Descomprimí el zip entero antes de ejecutarlo.
goto :fail

:backupfail
rem Half a copy must not pass for a whole one the next time.
rmdir /S /Q "%BACKUP%"
echo.
echo  No pude guardar la copia de tu partida. No se cambió nada.
goto :fail

:copyfail
echo.
echo  No pude copiar los archivos. Tu partida está guardada en la carpeta de arriba:
echo  ejecutá "2-volver-a-mi-partida.cmd" para dejar todo como estaba.

:fail
echo.
pause
exit /b 1
