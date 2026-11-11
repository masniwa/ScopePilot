@echo off
setlocal
set "SCOPEPILOT_APP_ROOT=%~dp0"
set "SCOPEPILOT_EXECUTABLE=%~dp0bin\Release\net10.0-windows\ScopePilot.exe"
if not exist "%SCOPEPILOT_EXECUTABLE%" set "SCOPEPILOT_EXECUTABLE=%~dp0bin\Release-review\net10.0-windows\ScopePilot.exe"
if not defined SCOPEPILOT_BURP_PROXY_JAR if exist "%~dp0..\mcp-server\libs\mcp-proxy-all.jar" set "SCOPEPILOT_BURP_PROXY_JAR=%~dp0..\mcp-server\libs\mcp-proxy-all.jar"

if not exist "%SCOPEPILOT_EXECUTABLE%" (
    echo ScopePilot Fork executable was not found. Build ScopePilot.csproj in Release first:
    echo "%SCOPEPILOT_EXECUTABLE%"
    exit /b 1
)

if exist "%~dp0.cache\dotnet\dotnet.exe" (
    set "DOTNET_ROOT=%~dp0.cache\dotnet"
    set "DOTNET_ROOT_X64=%~dp0.cache\dotnet"
    set "DOTNET_ROOT_ARM64=%~dp0.cache\dotnet"
)

if defined SCOPEPILOT_NODE_PATH if not exist "%SCOPEPILOT_NODE_PATH%" set "SCOPEPILOT_NODE_PATH="
if defined SCOPEPILOT_NODE_PATH goto node_ready
for %%I in ("%SCOPEPILOT_EXECUTABLE%") do set "SCOPEPILOT_BINARY_ROOT=%%~dpI"
if exist "%SCOPEPILOT_BINARY_ROOT%tools\node\node.exe" (
    set "SCOPEPILOT_NODE_PATH=%SCOPEPILOT_BINARY_ROOT%tools\node\node.exe"
    goto node_ready
)
if exist "%ProgramFiles%\nodejs\node.exe" (
    set "SCOPEPILOT_NODE_PATH=%ProgramFiles%\nodejs\node.exe"
    goto node_ready
)
if exist "%LOCALAPPDATA%\Programs\nodejs\node.exe" (
    set "SCOPEPILOT_NODE_PATH=%LOCALAPPDATA%\Programs\nodejs\node.exe"
    goto node_ready
)
for /f "delims=" %%I in ('where node.exe 2^>nul') do (
    set "SCOPEPILOT_NODE_PATH=%%I"
    goto node_ready
)
if exist "%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe" set "SCOPEPILOT_NODE_PATH=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe"

:node_ready
if defined SCOPEPILOT_NODE_PATH for %%I in ("%SCOPEPILOT_NODE_PATH%") do set "PATH=%%~dpI;%PATH%"
start "" /D "%SCOPEPILOT_APP_ROOT%" "%SCOPEPILOT_EXECUTABLE%"
exit /b %ERRORLEVEL%
