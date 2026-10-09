@echo off
setlocal
if "%~1"=="" (
    echo Arraste o arquivo pncpking.db de versao 34 para cima deste .cmd.
    echo O resultado sera criado ao lado do original, com sufixo -v33.db.
    pause
    exit /b 2
)
if not exist "%~1" (
    echo Arquivo de origem nao encontrado: "%~1"
    pause
    exit /b 2
)
set "PYTHON=py -3"
py -3 --version >nul 2>&1
if errorlevel 1 set "PYTHON=python"
%PYTHON% "%~dp0downgrade_schema_34_to_33.py" "%~1" "%~dpn1-v33.db"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo Conversao nao concluida. Nenhum banco foi substituido.
pause
exit /b %RESULT%
