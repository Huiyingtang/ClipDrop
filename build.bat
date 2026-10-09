@echo off
setlocal
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe

"%CSC%" /nologo /target:winexe /codepage:65001 /out:"%~dp0ClipDrop.exe" ^
  /win32icon:"%~dp0logo.ico" /resource:"%~dp0logo.ico",ClipDropIcon.ico ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "%~dp0ClipDrop.cs"

if errorlevel 1 (
  echo Build FAILED
) else (
  echo Build OK -^> %~dp0ClipDrop.exe
)
pause
