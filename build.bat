@echo off
setlocal EnableDelayedExpansion

rem  build.bat  -  build the wowmaps extractor + tile viewer
rem
rem    build              build Debug (what "dotnet run" uses)
rem    build release      build Release
rem    build run          build Debug and launch the GUI
rem    build release run  build Release and launch the GUI
rem    build viewer       also build the standalone native .wmesh viewer
rem    build clean        delete bin and obj first
rem    build publish      one-file dist\MapExtract.exe, nothing else to copy
rem    build publish run  publish and launch it
rem    build refresh      re-fetch db2index.csv + transports.csv from the listfile

rem  Capture the script directory BEFORE parsing args: "shift" also shifts %0,
rem  so %~dp0 is meaningless once the loop below has run.
set "ROOT=%~dp0"

set "CONFIG=Debug"
set "DORUN="
set "DOVIEWER="
set "DOCLEAN="
set "DOPUBLISH="
set "DOREFRESH="

:args
if "%~1"=="" goto checks
if /i "%~1"=="release" set "CONFIG=Release"
if /i "%~1"=="debug"   set "CONFIG=Debug"
if /i "%~1"=="run"     set "DORUN=1"
if /i "%~1"=="viewer"  set "DOVIEWER=1"
if /i "%~1"=="clean"   set "DOCLEAN=1"
if /i "%~1"=="publish" set "DOPUBLISH=1"
if /i "%~1"=="refresh" set "DOREFRESH=1"
shift
goto args

:checks
rem  publish is always Release; saying "Debug" in the banner would be a lie
if defined DOPUBLISH set "CONFIG=Release (publish)"
echo.
echo  wowmaps build  [%CONFIG%]
if defined DOPUBLISH set "CONFIG=Release"
echo  ---------------------------------------------------------------

rem ---------------------------------------------------------------- dotnet
where dotnet >nul 2>&1
if errorlevel 1 (
  if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set "PATH=%ProgramFiles%\dotnet;%PATH%"
  ) else (
    echo.
    echo  [X] The .NET SDK was not found.
    echo.
    echo      Install it with:
    echo          winget install --id Microsoft.DotNet.SDK.10
    echo.
    echo      Or download it from https://dotnet.microsoft.com/download
    echo      Then open a NEW terminal so PATH picks it up.
    echo.
    exit /b 1
  )
)

rem  A machine can have the .NET *runtime* without the SDK - that runs apps but
rem  cannot build them, and the error you get otherwise is not obvious.
set "HAVESDK="
for /f "delims=" %%L in ('dotnet --list-sdks 2^>nul') do set "HAVESDK=1"
if not defined HAVESDK (
  echo.
  echo  [X] .NET is installed but only the runtime, not the SDK.
  echo      You can run apps but not build them.
  echo.
  echo      Install the SDK with:
  echo          winget install --id Microsoft.DotNet.SDK.10
  echo.
  exit /b 1
)

rem  The project targets net10.0, so an older SDK will fail with a confusing
rem  "framework not supported" message.
set "SDK10="
for /f "tokens=1 delims=." %%V in ('dotnet --list-sdks 2^>nul') do (
  if %%V GEQ 10 set "SDK10=1"
)
if not defined SDK10 (
  echo.
  echo  [X] No .NET 10 SDK found. This project targets net10.0.
  echo      Installed SDKs:
  for /f "delims=" %%L in ('dotnet --list-sdks 2^>nul') do echo          %%L
  echo.
  echo      Install .NET 10 with:
  echo          winget install --id Microsoft.DotNet.SDK.10
  echo.
  exit /b 1
)
echo  [ok] .NET SDK

rem ---------------------------------------------------------------- CascLib
rem  NuGet's CascLib cannot read a 12.x root manifest, so we build TOM_RUS's
rem  source as a sibling checkout instead.
if not exist "%ROOT%..\CascLib-src\CascLib\CascLib.csproj" (
  echo.
  echo  [X] CascLib source not found next to this folder.
  echo      Expected: %ROOT%..\CascLib-src\CascLib\CascLib.csproj
  echo.
  echo      This project builds CascLib from source on purpose: the NuGet
  echo      package is too old to read a 12.x CASC root manifest.
  echo.
  echo      Get it with:
  echo          git clone https://github.com/WoW-Tools/CascLib.git "%ROOT%..\CascLib-src"
  echo.
  exit /b 1
)
echo  [ok] CascLib source

rem ---------------------------------------------------------------- listfile
rem  db2index.csv and transports.csv are slices of wowdev's community listfile, not
rem  ours to ship. They are fetched after the build instead, and regenerated on
rem  demand with "build refresh" when a patch moves FileDataIDs.
set "NEEDINDEX="
if not exist "%ROOT%db2index.csv" set "NEEDINDEX=1"
if not exist "%ROOT%transports.csv" set "NEEDINDEX=1"
if defined DOREFRESH set "NEEDINDEX=1"
if defined NEEDINDEX (
  echo  [--] index files will be fetched from the community listfile after the build
) else (
  echo  [ok] db2index.csv + transports.csv
)

rem ------------------------------------------------------------ file locks
rem  A running instance holds bin\%CONFIG%\net10.0\MapExtract.exe open and the
rem  build fails on the copy step - which looks like the code silently not
rem  changing. Catch it up front.
rem  Full paths: a Git Bash / MSYS shell on PATH shadows find and findstr.
"%SystemRoot%\System32\tasklist.exe" /FI "IMAGENAME eq MapExtract.exe" 2>nul | "%SystemRoot%\System32\findstr.exe" /I /C:"MapExtract.exe" >nul
if not errorlevel 1 (
  echo.
  echo  [X] MapExtract.exe is already running and holds the output file open.
  echo      Close the app window, then run this again.
  echo.
  echo      Or stop it now with:
  echo          taskkill /IM MapExtract.exe /F
  echo.
  exit /b 1
)

rem ---------------------------------------------------------------- build
if defined DOCLEAN (
  echo  [..] cleaning bin and obj
  if exist "%ROOT%bin" rmdir /s /q "%ROOT%bin"
  if exist "%ROOT%obj" rmdir /s /q "%ROOT%obj"
)

pushd "%ROOT%"
if defined DOPUBLISH (
  rem  Single file for the managed side. Native glfw3/cimgui stay as loose files:
  rem  Silk.NET resolves natives itself and does not look inside .NET's self-extract
  rem  directory, so embedding them makes the window platform "not applicable".
  echo  [..] publishing single-file to dist\
  echo.
  if exist "%ROOT%dist" rmdir /s /q "%ROOT%dist"
  dotnet publish -c Release -r win-x64 --self-contained false ^
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false ^
      -p:DebugType=none -o "%ROOT%dist" --nologo -v minimal
  set "RC=!ERRORLEVEL!"
) else (
  echo  [..] building %CONFIG%
  echo.
  dotnet build -c %CONFIG% --nologo -v minimal
  set "RC=!ERRORLEVEL!"
)
popd

if not "%RC%"=="0" (
  echo.
  echo  [X] Build failed.
  echo.
  echo      If the error mentions MSB3021 / MSB3027 "cannot access the file",
  echo      an instance is still running - close it and try again.
  echo.
  echo      If it mentions a missing package, restore with:
  echo          dotnet restore
  echo.
  exit /b %RC%
)

rem --------------------------------------------------- listfile-derived indexes
if defined NEEDINDEX (
  echo.
  echo  [..] fetching db2index.csv + transports.csv from the community listfile
  if defined DOPUBLISH (
    rem  %ROOT% ends in a backslash; "%ROOT%" would escape the closing quote
    "%ROOT%dist\MapExtract.exe" --makeindex "%ROOT:~0,-1%"
  ) else (
    "%ROOT%bin\%CONFIG%\net10.0\MapExtract.exe" --makeindex "%ROOT:~0,-1%"
  )
  if exist "%ROOT%db2index.csv" (
    rem  the build copied the old or absent files already, so refresh the outputs
    if exist "%ROOT%bin\%CONFIG%\net10.0" (
      copy /y "%ROOT%db2index.csv" "%ROOT%bin\%CONFIG%\net10.0\" >nul 2>&1
      copy /y "%ROOT%transports.csv" "%ROOT%bin\%CONFIG%\net10.0\" >nul 2>&1
    )
    if exist "%ROOT%dist" (
      copy /y "%ROOT%db2index.csv" "%ROOT%dist\" >nul 2>&1
      copy /y "%ROOT%transports.csv" "%ROOT%dist\" >nul 2>&1
    )
  ) else (
    echo  [X] index fetch failed - DB2 lookups will fall back to hardcoded ids.
    echo      Check your connection and run "build refresh".
  )
)

echo.
if defined DOPUBLISH (
  echo  [ok] published  dist\MapExtract.exe
  echo       plus glfw3.dll, cimgui.dll, db2index.csv
  echo       exports default to dist\out\
) else (
  echo  [ok] built  bin\%CONFIG%\net10.0\MapExtract.exe
)

rem ----------------------------------------------------- native viewer (opt)
if defined DOVIEWER (
  echo.
  echo  [..] standalone native viewer
  where cmake >nul 2>&1
  if errorlevel 1 (
    if exist "C:\msys64\ucrt64\bin\cmake.exe" (
      set "PATH=C:\msys64\ucrt64\bin;!PATH!"
    ) else (
      echo  [X] cmake not found - skipping the native viewer.
      echo      It needs the MSYS2 UCRT64 toolchain. In an MSYS2 shell:
      echo          pacman -S --needed mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-ninja mingw-w64-ucrt-x86_64-SDL2 mingw-w64-ucrt-x86_64-SDL2_ttf
      echo      MSYS2 itself: winget install --id MSYS2.MSYS2
      goto done
    )
  )
  pushd "%ROOT%viewer"
  cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_PREFIX_PATH=C:/msys64/ucrt64
  if not errorlevel 1 cmake --build build
  popd
  if exist "%ROOT%viewer\build\wmeshview.exe" (
    echo  [ok] built  viewer\build\wmeshview.exe
  ) else (
    echo  [X] native viewer build failed - see the output above.
  )
)

:done
if defined DORUN (
  echo.
  echo  [..] launching
  if defined DOPUBLISH (
    start "" "%ROOT%dist\MapExtract.exe"
  ) else (
    start "" "%ROOT%bin\%CONFIG%\net10.0\MapExtract.exe"
  )
)

echo.
endlocal
exit /b 0
