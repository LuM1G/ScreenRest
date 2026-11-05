@echo off
rem Build ScreenRest.exe using the C# compiler shipped with Windows.
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo .NET Framework 4 csc.exe not found.
    exit /b 1
)
"%CSC%" /nologo /target:winexe /out:"%~dp0ScreenRest.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "%~dp0ScreenRest.cs"
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)
echo Build OK: %~dp0ScreenRest.exe
