@echo off
rem Builds src\Harness\bin\Harness.exe with MSVC (x64). Run from any directory.
setlocal
set "HERE=%~dp0"
set "VCVARS=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if exist "%VCVARS%" goto havevc
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VCVARS=%%i\VC\Auxiliary\Build\vcvars64.bat"
:havevc
call "%VCVARS%" >nul 2>&1 || exit /b 1
if not exist "%HERE%bin" mkdir "%HERE%bin"
pushd "%HERE%bin"
cl /nologo /O2 /EHsc /std:c++17 /W4 /wd4100 /DUNICODE /D_UNICODE /D_CRT_SECURE_NO_WARNINGS /utf-8 /MT "%HERE%harness.cpp" /Fe:Harness.exe /link /SUBSYSTEM:CONSOLE
set ERR=%ERRORLEVEL%
popd
exit /b %ERR%
