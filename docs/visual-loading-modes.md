# Switch Visual between native loading and AddinLoader

These steps apply to the current deployment on this machine. Use the folder for the Revit year you are switching, for example `%APPDATA%\Autodesk\Revit\Addins\2025`. Show file name extensions in Explorer before renaming files.

Revit must restart when switching modes. Reload All cannot unload the native add-in from the process.

## Use native Visual loading

1. Save your work and close that Revit version.
2. Open `%APPDATA%\Autodesk\Revit\Addins\<year>`.
3. Rename `_VisualRevitAddin.addin` to `_VisualRevitAddin.addin.loader-mode`. In the current setup, this file contains the **AddinLoader** startup registration.
4. Rename `_VisualRevitAddin.addin.addinloader-disabled` to `_VisualRevitAddin.addin`. This restores the **VisualRevitAddin.Application** registration. For 2022, there is no saved per-user Visual manifest; skip this step and let Revit use its existing machine-wide Visual manifest.
5. Leave `AddinLoader.addin.loader-alias-disabled` disabled. Leave the DLL folders and loader settings in place.
6. Start Revit. Visual loads directly from its deployed add-in folder. The AddinLoader panel on the Add-Ins tab should be absent.

## Return to AddinLoader

1. Save your work and close that Revit version.
2. In the same folder, rename the restored native `_VisualRevitAddin.addin` to `_VisualRevitAddin.addin.addinloader-disabled`. Skip this step for 2022 if no per-user native manifest exists.
3. Rename `_VisualRevitAddin.addin.loader-mode` back to `_VisualRevitAddin.addin`.
4. Leave `AddinLoader.addin.loader-alias-disabled` disabled. The active loader manifest uses the Visual filename to override the machine-wide Visual manifest and prevent both copies starting.
5. Start Revit. The AddinLoader panel appears on the Add-Ins tab and Visual starts through the loader.
6. Open **AddinLoader → Application Loader** and confirm the watched DLL is `C:\Users\LJ\source\repos\Revit\VisualRevitAddin\bin\Debug Rxx\VisualRevitAddin.dll` for that version, with **Auto Reload** enabled. For Revit 2025, use `Debug R25`.

Build a watched Debug DLL without replacing the active startup manifest. From the Revit repository, for example:

```powershell
dotnet build VisualRevitAddin/VisualRevitAddin.csproj -c "Debug R25" -p:DeployAddin=false -p:PublishAddin=false -p:RepackAddin=false
```

The native mode uses the DLL referenced by its restored manifest; the loader mode uses the watched Debug DLL. Changing the loader's own code requires restarting Revit. Rebuilding Visual while using the loader triggers automatic reload.

If a filename is missing or already exists under the destination name, inspect the manifests before overwriting: the loader registration has `FullClassName` = `AddinLoader.App`; native Visual has `FullClassName` = `VisualRevitAddin.Application`. Original deployment backups are under `artifacts/validation/deployment-backup/` in the AddinLoader repository.
