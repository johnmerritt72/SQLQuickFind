# SSMS 22 Extension Development Guide

**Purpose:** Step-by-step guide for building a VSIX extension that loads in SQL Server Management Studio 22. This guide was written from hard-won experience building the SQLParity extension, after encountering every pitfall in the SSMS 22 extensibility story. Follow this guide to avoid days of troubleshooting.

**Audience:** Developers who want to build custom SSMS 22 extensions using Visual Studio 2022.

**Last updated:** 2026-04-11

---

## Overview

SSMS 22 is rebased on the Visual Studio 2022 shell (internally version 18.0), but it is NOT Visual Studio. It has its own:
- Product ID (`Microsoft.VisualStudio.Ssms`)
- Installation path (`C:\Program Files\Microsoft SQL Server Management Studio 22\Release\`)
- User data hive (`%LOCALAPPDATA%\Microsoft\SSMS\22.0_<instanceId>\`)
- VSIXInstaller instance
- Extension discovery mechanism

Extensions built for Visual Studio will NOT automatically work in SSMS 22. This guide covers every difference.

---

## Prerequisites

- **Visual Studio 2022** (any edition: Community, Professional, or Enterprise)
- **"Visual Studio extension development" workload** installed via VS Installer
- **SSMS 22** installed
- **.NET Framework 4.8 Targeting Pack** (installed with VS 2022)

---

## Step 1: Create the VSIX Project

1. Open Visual Studio 2022
2. File → New → Project → search "Empty VSIX Project" → select it → Create
3. Name it appropriately (e.g., `MyExtension`)
4. Set the Location to your desired path

This creates an old-style (non-SDK) csproj with `ToolsVersion="15.0"`. **Do not** try to convert it to SDK-style — the VSSDK build tools require the old format.

---

## Step 2: Configure the Project File (.csproj)

The template generates a csproj with several properties set to `false` that need to be `true`. Open the csproj and make these changes:

### Required Property Changes

```xml
<PropertyGroup>
  <!-- Change these from false to true -->
  <GeneratePkgDefFile>true</GeneratePkgDefFile>
  <UseCodebase>true</UseCodebase>  <!-- CRITICAL: Without this, SSMS can't find your DLL -->
  <IncludeAssemblyInVSIXContainer>true</IncludeAssemblyInVSIXContainer>
  <IncludeDebugSymbolsInVSIXContainer>true</IncludeDebugSymbolsInVSIXContainer>
  <IncludeDebugSymbolsInLocalVSIXDeployment>true</IncludeDebugSymbolsInLocalVSIXDeployment>
  <CopyBuildOutputToOutputDirectory>true</CopyBuildOutputToOutputDirectory>
  <CopyOutputSymbolsToOutputDirectory>true</CopyOutputSymbolsToOutputDirectory>

  <!-- Add this for modern C# features -->
  <LangVersion>latest</LangVersion>
</PropertyGroup>
```

### Why `UseCodebase` Matters

Without `<UseCodebase>true</UseCodebase>`, the pkgdef generator creates an `"Assembly"` entry:
```
"Assembly"="MyExtension, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
```
This tells SSMS to load the assembly from the GAC, which fails because your extension isn't in the GAC.

With `<UseCodebase>true</UseCodebase>`, the pkgdef generates a `"CodeBase"` entry:
```
"CodeBase"="$PackageFolder$\MyExtension.dll"
```
This tells SSMS to load from the extension's install directory, which is how all SSMS extensions work.

### WPF References (if using WPF UI)

Old-style csproj doesn't auto-reference WPF assemblies. Add them explicitly:

```xml
<ItemGroup>
  <Reference Include="PresentationCore" />
  <Reference Include="PresentationFramework" />
  <Reference Include="System.Xaml" />
  <Reference Include="WindowsBase" />
  <Reference Include="System.Design" />
</ItemGroup>
```

### File Discovery

**Old-style csproj does NOT auto-discover files.** Every `.cs` file must be listed:

```xml
<ItemGroup>
  <Compile Include="MyPackage.cs" />
  <Compile Include="MyCommand.cs" />
  <Compile Include="Views\MyView.xaml.cs">
    <DependentUpon>MyView.xaml</DependentUpon>
  </Compile>
</ItemGroup>
```

Every `.xaml` file must be listed as a Page:

```xml
<ItemGroup>
  <Page Include="Views\MyView.xaml">
    <Generator>MSBuild:Compile</Generator>
    <SubType>Designer</SubType>
  </Page>
</ItemGroup>
```

Every `.vsct` file must be listed:

```xml
<ItemGroup>
  <VSCTCompile Include="MyPackage.vsct">
    <ResourceName>Menus.ctmenu</ResourceName>
  </VSCTCompile>
</ItemGroup>
```

---

## Step 3: Configure the VSIX Manifest

This is the **most important file** for SSMS compatibility. Open `source.extension.vsixmanifest` and configure:

```xml
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0"
    xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011"
    xmlns:d="http://schemas.microsoft.com/developer/vsx-schema-design/2011">
  <Metadata>
    <Identity Id="MyExtension.GUID-HERE" Version="1.0.0"
              Language="en-US" Publisher="Your Name" />
    <DisplayName>My Extension</DisplayName>
    <Description xml:space="preserve">Description here.</Description>
    <Tags>ssms</Tags>
  </Metadata>

  <!-- CRITICAL: Target Microsoft.VisualStudio.Ssms, NOT Community/Professional/Enterprise -->
  <Installation AllUsers="true">
    <InstallationTarget Id="Microsoft.VisualStudio.Ssms" Version="[21.0,23.0)">
      <ProductArchitecture>amd64</ProductArchitecture>
    </InstallationTarget>
  </Installation>

  <Dependencies>
    <Dependency Id="Microsoft.Framework.NDP" DisplayName="Microsoft .NET Framework"
                d:Source="Manual" Version="[4.5,)" />
  </Dependencies>

  <Assets>
    <Asset Type="Microsoft.VisualStudio.VsPackage" d:Source="Project"
           d:ProjectName="%CurrentProject%"
           Path="|%CurrentProject%;PkgdefProjectOutputGroup|" />
  </Assets>

  <Prerequisites>
    <Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor"
                  Version="[17.0,19.0)"
                  DisplayName="Visual Studio core editor" />
  </Prerequisites>
</PackageManifest>
```

### Key Points

- **`InstallationTarget Id` MUST be `Microsoft.VisualStudio.Ssms`** — not `Microsoft.VisualStudio.Community` or `Microsoft.VisualStudio.Pro`. SSMS identifies itself as a different product.
- **Version range `[21.0,23.0)`** covers both SSMS 21 and SSMS 22.
- **`AllUsers="true"`** installs to the Program Files directory, which is how SSMS extensions are typically deployed.

---

## Step 4: Create the Package Class

```csharp
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace MyExtension
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string,
                     PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class MyPackage : AsyncPackage
    {
        public const string PackageGuidString = "YOUR-GUID-HERE";

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            // Initialize your commands here
        }
    }
}
```

### Required Attributes

| Attribute | Purpose |
|-----------|---------|
| `[PackageRegistration(...)]` | Registers the package with the VS shell |
| `[Guid(...)]` | Unique package identifier |
| `[ProvideMenuResource("Menus.ctmenu", 1)]` | Registers the command table (.vsct) |
| `[ProvideAutoLoad(ShellInitialized, BackgroundLoad)]` | **CRITICAL for SSMS**: Loads the package when SSMS starts. Without this, SSMS may not discover your menu commands. |

---

## Step 5: Create the Command Table (.vsct)

The `.vsct` file defines menu items, toolbar buttons, and keyboard shortcuts.

```xml
<?xml version="1.0" encoding="utf-8"?>
<CommandTable xmlns="http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable"
              xmlns:xs="http://www.w3.org/2001/XMLSchema">
  <Extern href="stdidcmd.h" />
  <Extern href="vsshlids.h" />

  <Commands package="guidMyPackage">
    <Groups>
      <Group guid="guidMyCmdSet" id="MyMenuGroup" priority="0x0600">
        <Parent guid="guidSHLMainMenu" id="IDM_VS_MENU_TOOLS" />
      </Group>
    </Groups>

    <Buttons>
      <Button guid="guidMyCmdSet" id="MyCommandId" priority="0x0100" type="Button">
        <Parent guid="guidMyCmdSet" id="MyMenuGroup" />
        <Strings>
          <ButtonText>My Command</ButtonText>
        </Strings>
      </Button>
    </Buttons>
  </Commands>

  <Symbols>
    <!-- MUST match the PackageGuidString in your package class -->
    <GuidSymbol name="guidMyPackage" value="{YOUR-PACKAGE-GUID}" />
    <GuidSymbol name="guidMyCmdSet" value="{YOUR-COMMANDSET-GUID}">
      <IDSymbol name="MyMenuGroup" value="0x1020" />
      <IDSymbol name="MyCommandId" value="0x0100" />
    </GuidSymbol>
  </Symbols>
</CommandTable>
```

### Important: GUID Matching

The `guidMyPackage` symbol value in the `.vsct` **must exactly match** the `PackageGuidString` in the C# package class. A mismatch means the menu items won't appear.

---

## Step 6: Building

### From the Command Line

The VSIX project requires legacy MSBuild, not `dotnet build`:

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" MySolution.sln -t:Build -p:Configuration=Debug -v:minimal
```

(Adjust the VS edition path: `Community`, `Professional`, or `Enterprise`.)

`dotnet build` will compile the code but fail at the VSSDK deployment step with: `VSIX deployment is not supported with 'dotnet build'`.

### From Visual Studio

Ctrl+Shift+B works as normal.

---

## Step 7: Installing to SSMS 22

This is where most guides fall apart. The standard VSSDK `DeployExtension` target deploys to the **Visual Studio** experimental hive, not to SSMS. You must use the VSIXInstaller manually.

### Finding Your SSMS 22 Instance ID

Read `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\SSMS.isolation.ini`:

```ini
InstallationID=919b8d66    ← This is your instance ID
SKU=SSMS
```

### Installing via VSIXInstaller

**Always run from PowerShell** (not bash — forward slashes get misinterpreted as flags):

```powershell
# Install to SSMS 22
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" `
    /instanceIds:YOUR_INSTANCE_ID `
    "C:\path\to\your\Extension.vsix"
```

For the experimental hive (debug builds):

```powershell
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" `
    /instanceIds:YOUR_INSTANCE_ID `
    /rootSuffix:Exp `
    "C:\path\to\your\Extension.vsix"
```

### Uninstalling

```powershell
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" `
    /uninstall:YOUR_EXTENSION_ID
```

The extension ID is the `Identity Id` from your `source.extension.vsixmanifest`.

### Important Notes

- **VSIXInstaller is a GUI application.** It returns exit code 0 immediately while the actual install runs in the background. Wait for the GUI dialog to show completion.
- **If you have multiple SSMS versions installed**, use `/instanceIds` to target the correct one. Without it, the installer may install to SSMS 21 instead of 22.
- **`/appIdInstallPath` requires additional flags** (`/appIdName`, `/skuName`, `/skuVersion`). Using `/instanceIds` is simpler and more reliable.

---

## Step 8: Debugging with F5

### Recommended F5 Configuration

In the VSIX project properties → Debug:
- **Start external program:** `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\SSMS.exe`
- **Command line arguments:** `/rootsuffix Exp`

Or set in the csproj:

```xml
<StartAction>Program</StartAction>
<StartProgram>C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\SSMS.exe</StartProgram>
<StartArguments>/rootsuffix Exp</StartArguments>
```

### The F5 Problem

F5 from Visual Studio will:
1. Build the VSIX ✅
2. Deploy it to the **VS 2022** experimental hive ❌ (not the SSMS hive)
3. Launch SSMS 22 with `/rootsuffix Exp` ✅

SSMS 22 uses `%LOCALAPPDATA%\Microsoft\SSMS\22.0_<instanceId>Exp\`, but the VSSDK deploys to `%LOCALAPPDATA%\Microsoft\VisualStudio\17.0_<vsInstanceId>Exp\`.

### Recommended Development Workflow

Since F5 deployment doesn't target the right hive, use this workflow:

1. **Build** in Visual Studio (Ctrl+Shift+B)
2. **Install** via PowerShell:
   ```powershell
   # Uninstall previous version
   & "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" /uninstall:YOUR_EXTENSION_ID

   # Wait for GUI to complete, then install new version
   & "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" /instanceIds:YOUR_INSTANCE_ID "path\to\your.vsix"
   ```
3. **Launch SSMS 22** normally (or via F5 which just starts SSMS)
4. **Test** your extension
5. **Close SSMS** before the next build cycle

You can automate steps 1-2 with a post-build script.

---

## Step 9: Reading the Activity Log

When your extension fails to load, SSMS writes to an Activity Log. Enable it by launching SSMS with `/log`:

```
SSMS.exe /rootsuffix Exp /log
```

The log location is: `%APPDATA%\Microsoft\SSMS\22.0_<instanceId>[Exp]\ActivityLog.xml`

**Note:** This file is UTF-16 encoded. Read it with PowerShell:

```powershell
Get-Content "path\to\ActivityLog.xml" -Encoding Unicode |
    Select-String -Pattern "YourExtensionName|YourPackageGuid"
```

---

## Common Pitfalls

### 1. "The package did not load correctly"

**Cause:** Almost always a `FileNotFoundException` — SSMS found the pkgdef but can't find the DLL.

**Fix:** Ensure `<UseCodebase>true</UseCodebase>` is in your csproj. Rebuild and reinstall the VSIX.

### 2. Menu item doesn't appear

**Possible causes:**
- Extension installed to the wrong SSMS version (check `/instanceIds`)
- Manifest targets `Microsoft.VisualStudio.Community` instead of `Microsoft.VisualStudio.Ssms`
- GUID mismatch between `.vsct` and package class
- Missing `[ProvideAutoLoad]` attribute

### 3. Extension installed but not visible

**Cause:** SSMS 22 removed the "Manage Extensions" menu. You can't verify installation through the UI.

**Verification:** Check if your DLL exists under:
- `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\<randomfolder>\`

### 4. Build succeeds but `dotnet build` fails

**Cause:** Normal. VSIX projects require legacy MSBuild. Use the MSBuild.exe path from your VS installation.

### 5. Extension works in VS but not in SSMS

**Cause:** Different product IDs, different hive paths, different extension discovery. Follow this guide from the beginning.

---

## SSMS 22 Technical Details

| Property | Value |
|----------|-------|
| Product ID | `Microsoft.VisualStudio.Ssms` |
| Shell version | 18.0 (internally) |
| Install path | `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\` |
| User hive | `%LOCALAPPDATA%\Microsoft\SSMS\22.0_<instanceId>\` |
| Exp hive | `%LOCALAPPDATA%\Microsoft\SSMS\22.0_<instanceId>Exp\` |
| Activity log | `%APPDATA%\Microsoft\SSMS\22.0_<instanceId>[Exp]\ActivityLog.xml` |
| Isolation config | `Common7\IDE\SSMS.isolation.ini` |
| VSIXInstaller | `Common7\IDE\VSIXInstaller.exe` |
| Target framework | .NET Framework 4.7.2+ (4.8 recommended) |
| VS SDK NuGet | `Microsoft.VisualStudio.SDK` 17.0+ |

---

## Minimal Working Example

For a complete minimal working example, see the SQLParity VSIX project at `src/SQLParity.Vsix/` in this repository, or the SSMSLoggingExtension at `C:\Code\SSMSLoggingExtension\SSMSLogger\`.
