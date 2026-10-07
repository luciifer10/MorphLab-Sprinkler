; MorphLab Sprinkler Designer — one EXE for Revit 2023–2026 (same approach as your Electrical / Smart Annotation installers)
; Build each configuration first (Release R23..R26), then compile this script in Inno Setup.

#define AppName "MorphLab Sprinkler Designer"
#define AppVersion "1.1.0"
#define Src "D:\Dev\MorphLab.Sprinkler\bin"

[Setup]
AppId={{6E1C3B7A-4F2D-4C8E-9A51-2B7D90F3C611}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=MorphLab
DefaultDirName={userappdata}\Autodesk\Revit\Addins
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=C:\MorphLabSprinklerInstaller
OutputBaseFilename=MorphLabSprinkler_{#AppVersion}_Setup
Compression=lzma2
SolidCompression=yes

[Components]
Name: "r2023"; Description: "Revit 2023"; Types: full
Name: "r2024"; Description: "Revit 2024"; Types: full
Name: "r2025"; Description: "Revit 2025"; Types: full
Name: "r2026"; Description: "Revit 2026"; Types: full

[Files]
Source: "{#Src}\Release R23\MorphLab.Sprinkler.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2023\MorphLab.Sprinkler"; Components: r2023; Flags: ignoreversion
Source: "..\MorphLab.Sprinkler.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2023"; Components: r2023; Flags: ignoreversion
Source: "{#Src}\Release R24\MorphLab.Sprinkler.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\MorphLab.Sprinkler"; Components: r2024; Flags: ignoreversion
Source: "..\MorphLab.Sprinkler.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: r2024; Flags: ignoreversion
Source: "{#Src}\Release R25\MorphLab.Sprinkler.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\MorphLab.Sprinkler"; Components: r2025; Flags: ignoreversion
Source: "..\MorphLab.Sprinkler.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: r2025; Flags: ignoreversion
Source: "{#Src}\Release R26\MorphLab.Sprinkler.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\MorphLab.Sprinkler"; Components: r2026; Flags: ignoreversion
Source: "..\MorphLab.Sprinkler.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: r2026; Flags: ignoreversion
; built-in sprinkler families
Source: "..\Families\*.rfa"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2023\MorphLab.Sprinkler\Families"; Components: r2023; Flags: ignoreversion
Source: "..\Families\*.rfa"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\MorphLab.Sprinkler\Families"; Components: r2024; Flags: ignoreversion
Source: "..\Families\*.rfa"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\MorphLab.Sprinkler\Families"; Components: r2025; Flags: ignoreversion
Source: "..\Families\*.rfa"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\MorphLab.Sprinkler\Families"; Components: r2026; Flags: ignoreversion
