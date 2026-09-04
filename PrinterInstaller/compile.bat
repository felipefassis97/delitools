@echo off
title PrinterInstaller - Compilador
echo.
echo  =========================================
echo   PrinterInstaller - Gerando .EXE
echo  =========================================
echo.

REM -------------------------------------------------------
REM Busca o compilador C# do .NET Framework (ja vem no Windows)
REM Testa 64-bit primeiro, depois 32-bit
REM -------------------------------------------------------
set CSC=

for %%V in (v4.0.30319 v3.5) do (
    if exist "%SystemRoot%\Microsoft.NET\Framework64\%%V\csc.exe" (
        set CSC="%SystemRoot%\Microsoft.NET\Framework64\%%V\csc.exe"
        goto :compilar
    )
    if exist "%SystemRoot%\Microsoft.NET\Framework\%%V\csc.exe" (
        set CSC="%SystemRoot%\Microsoft.NET\Framework\%%V\csc.exe"
        goto :compilar
    )
)

echo  ERRO: Compilador nao encontrado.
echo  O .NET Framework 4.x nao esta instalado.
echo.
pause
exit /b 1

:compilar
echo  Compilador: %CSC%
echo  Compilando PrinterInstaller.cs...
echo.

REM Delitools.exe e o unico executavel oficial (e o nome empacotado pelo
REM instalador Inno Setup em Delitools_Setup.iss). Nao renomeie/duplique -
REM copias divergentes ja causaram bug de instalador desatualizado antes.
%CSC% /nologo /target:winexe /out:Delitools.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.ServiceProcess.dll PrinterInstaller.cs

echo.
if exist Delitools.exe (
    echo  =========================================
    echo   Delitools.exe criado com sucesso!
    echo.
    echo   Proximo passo:
    echo   De um duplo clique em Delitools.exe
    echo   (aceite o UAC se aparecer)
    echo  =========================================
) else (
    echo  FALHA na compilacao. Verifique os erros acima.
)

echo.
pause
