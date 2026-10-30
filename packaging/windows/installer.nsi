; Native x64/ARM64 payloads use the standard Unicode NSIS bootstrap.
; Windows runs the x86 installer stub via its built-in compatibility layer.
Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!ifndef VERSION
  !error "VERSION is required"
!endif
!ifndef PAYLOAD
  !error "PAYLOAD is required"
!endif
!ifndef OUTPUT
  !error "OUTPUT is required"
!endif
!ifndef REMOVE_FILES
  !error "REMOVE_FILES is required"
!endif
!ifndef APP_ICON
  !error "APP_ICON is required"
!endif
!ifndef ARCH
  !error "ARCH is required (arm64 or x64)"
!endif
!if "${ARCH}" == "arm64"
  !define ARCH_NAME "ARM64"
!else if "${ARCH}" == "x64"
  !define ARCH_NAME "x64"
!else
  !error "Unsupported ARCH: ${ARCH} (expected arm64 or x64)"
!endif
!define APP_REG_KEY "Software\RoboExpCE\${ARCH_NAME}"
!define UNINSTALL_REG_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\RoboExpCE-${ARCH_NAME}"
!define START_MENU_DIR "RoboExp CE ${ARCH_NAME}"

Name "RoboExp CE ${VERSION} (${ARCH_NAME})"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\RoboExp CE ${ARCH_NAME}"
InstallDirRegKey HKCU "${APP_REG_KEY}" "InstallDir"
RequestExecutionLevel user
ManifestSupportedOS all
ManifestLongPathAware true
SetCompressor /SOLID lzma
SetCompressorDictSize 32
BrandingText "RoboExp CE — Windows ${ARCH_NAME}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "RoboExp CE ${ARCH_NAME}"
VIAddVersionKey "FileDescription" "RoboExp CE Windows ${ARCH_NAME} Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "@Mxmilu 2026"
!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\RoboExp.App.exe"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!define MUI_LANGDLL_REGISTRY_ROOT HKCU
!define MUI_LANGDLL_REGISTRY_KEY "${APP_REG_KEY}"
!define MUI_LANGDLL_REGISTRY_VALUENAME "InstallerLanguage"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "TradChinese"

Function .onInit
  SetShellVarContext current
  SetRegView 64
  !insertmacro MUI_LANGDLL_DISPLAY
!if "${ARCH}" == "arm64"
  ${IfNot} ${IsNativeARM64}
    MessageBox MB_OK|MB_ICONSTOP "This package requires Windows ARM64. / 此安装包仅适用于 Windows ARM64。"
    SetErrorLevel 1633
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin11}
    MessageBox MB_OK|MB_ICONSTOP "Windows 11 ARM64 or later is required. / 需要 Windows 11 ARM64 或更高版本。"
    SetErrorLevel 1633
    Abort
  ${EndIf}
!else
  ${IfNot} ${IsNativeAMD64}
    MessageBox MB_OK|MB_ICONSTOP "This package requires native Windows x64 (AMD64). / 此安装包仅适用于原生 Windows x64 (AMD64)。"
    SetErrorLevel 1633
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Windows 10 x64 or later is required. / 需要 Windows 10 x64 或更高版本。"
    SetErrorLevel 1633
    Abort
  ${EndIf}
!endif
FunctionEnd

Section "RoboExp CE" MainSection
  SectionIn RO
  SetShellVarContext current
  SetRegView 64
  SetOutPath "$INSTDIR"
  File /r "${PAYLOAD}/*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\${START_MENU_DIR}"
  CreateShortcut "$SMPROGRAMS\${START_MENU_DIR}\RoboExp CE (${ARCH_NAME}).lnk" "$INSTDIR\RoboExp.App.exe"
  CreateShortcut "$SMPROGRAMS\${START_MENU_DIR}\Uninstall (${ARCH_NAME}).lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${APP_REG_KEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "DisplayName" "RoboExp CE (${ARCH_NAME})"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "Publisher" "Mxmilu"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "URLInfoAbout" "https://github.com/Mxmilu666/RoboEXP-CE"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "DisplayIcon" "$INSTDIR\RoboExp.App.exe"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${UNINSTALL_REG_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_REG_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  ; Generated explicit file list: never recursively remove an arbitrary install directory or user projects.
  !include "${REMOVE_FILES}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${START_MENU_DIR}\RoboExp CE (${ARCH_NAME}).lnk"
  Delete "$SMPROGRAMS\${START_MENU_DIR}\Uninstall (${ARCH_NAME}).lnk"
  RMDir "$SMPROGRAMS\${START_MENU_DIR}"
  DeleteRegKey HKCU "${UNINSTALL_REG_KEY}"
  DeleteRegKey HKCU "${APP_REG_KEY}"
  ; AppData settings/caches/imports and user projects are kept; bundled assets/toolchain are removed above.
SectionEnd
