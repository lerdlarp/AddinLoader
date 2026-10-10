# AddinLoader

Develop Revit add-ins **without closing Revit**. Build in Visual Studio, and the new build is running in
Revit seconds later: commands, ribbon panels, WPF windows, all of it.

AddinLoader is a clone of [ricaun's AppLoader](https://ricaun.com/AppLoader/) for our own use, with the same
workflow plus automatic reload of `IExternalApplication` ribbons.

## Features

After installation the default **Add-Ins** tab contains the **AddinLoader** panel and pulldown:

| Command | What it does |
|---|---|
| **Application Loader** | Opens the loader window. **Load** a `.dll` (or `.addin` manifest) and its commands and applications are listed. **Double-click** a command (or **Execute**) to run it. **Create** writes a new add-in project. **Reload** / **Remove** act on the selected add-in. |
| **Application Loader (Faceless)** | Runs the last command again, using the newest build. Give it a keyboard shortcut for a build → run loop. |
| **Console (Show/Hide)** | Dockable pane (right side) showing everything written with `Console.WriteLine` and `Debug.WriteLine`. Press **F5** in the pane to clear it. |
| **Reload All** | Reloads the newest build of every add-in in the list. |

* **Your build output is never locked.** Each load runs from a private copy, so Visual Studio can rebuild
  while Revit is open.
* **Automatic reload.** The build output folder is watched. When a new build lands, the add-in is reloaded
  the next time Revit is idle. Its `IExternalApplication.OnShutdown` is called, the ribbon tabs and panels it
  created are removed, and the new build's `OnStartup` creates them again. This can be turned off in the
  loader window. Commands always run the newest build regardless.
* **Remembered.** The list of add-ins is saved per Revit version and loaded again when Revit starts.
* **Debugging works.** `.pdb` files are rewritten with the assemblies, so breakpoints hit when Visual Studio
  is attached to `Revit.exe`.
* Revit **2020 – 2024** (.NET Framework 4.8), **2025 – 2026** (.NET 8), and **2027** (.NET 10).

## Install

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build all supported versions. In PowerShell:

```powershell
.\install.ps1 -RevitVersion 2024            # or several: -RevitVersion 2023,2024,2025
.\install.ps1 -RevitVersion (2020..2027)    # all supported versions
.\install.ps1 -RevitVersion 2024 -Uninstall
```

This builds `src/AddinLoader` for each version and copies it to
`%AppData%\Autodesk\Revit\Addins\<version>\` (`AddinLoader.addin` + `AddinLoader\` folder).
Pre-built binaries are also produced by CI as artifacts named `AddinLoader-Revit<version>`.

## Daily workflow with our main Revit app

For this machine's configured Visual deployment, follow [Switch Visual between native loading and
AddinLoader](docs/visual-loading-modes.md). It lists the exact manifest names and restart steps.

1. **Disable the deployed copy of the app** while developing (rename its `.addin` file in
   `%AppData%\Autodesk\Revit\Addins\<version>` or `%ProgramData%\Autodesk\Revit\Addins\<version>`).
   If both copies run, you get two ribbons and WPF may pick up resources from the deployed one.
2. Start Revit, choose **AddinLoader ▸ Application Loader ▸ Load**, and pick the app's
   `bin\Debug\...\OurApp.dll`. Its ribbon appears and its commands are listed.
3. Edit code and **Build** (Ctrl+Shift+B). There's no need to stop or start debugging. The ribbon is recreated from the new
   build, and the next command you run, from the ribbon, the loader window or *Faceless*, is the new code.
4. To debug, use *Debug ▸ Attach to Process ▸ Revit.exe* once and keep building. Breakpoints keep
   working across reloads.

To try it first, build `samples/SampleAddin` and load `samples/SampleAddin/bin/Debug/<version>/SampleAddin.dll`.
Change the text in `HelloWindow.xaml`, build, and click **Sample ▸ Hello** again.

## Writing add-ins that reload cleanly

* **Undo `OnStartup` in `OnShutdown`.** Unsubscribe events (`DocumentOpened`, `Idling`, …), unregister
  updaters (`UpdaterRegistry.UnregisterUpdater`), and dispose of `ExternalEvent`s. AddinLoader calls
  `OnShutdown` before every reload. Anything left subscribed keeps running the old code.
* **Use `Assembly.GetExecutingAssembly().Location`** for `PushButtonData`, as usual. Don't hard-code the path
  to `bin\Debug`.
* **Things Revit only allows during Revit's own startup can't be reloaded.** That covers `RegisterDockablePane`
  and handlers for `ControlledApplication.ApplicationInitialized`, which won't fire again. Restart Revit after changing them.
* Static state starts fresh on every reload, because each build is a new assembly.

## How it works

.NET can't unload an assembly from a running process, and loading the same assembly identity twice just
returns the old one. AddinLoader handles this as follows:

1. **Shadow copy.** It copies the whole output folder to
   `%LocalAppData%\AddinLoader\Shadow\<Name>\<pid>_<n>`. The original files stay unlocked.
2. **New identity per build.** Using [Mono.Cecil](https://github.com/jbevain/cecil), the add-in's own assemblies
   are renamed (`OurApp` becomes `OurApp.r3`, `OurApp.Core` becomes `OurApp.Core.r3`), along with everything that
   refers to the old names:
   * assembly references between the add-in's assemblies,
   * `InternalsVisibleTo`,
   * WPF pack URIs in code (`/OurApp;component/View.xaml`),
   * assembly prefixes used to locate WPF dictionaries during theme changes (`OurApp;`),
   * assembly-qualified type names,
   * the WPF `OurApp.g.resources` resource.

   Strong names are removed from renamed assemblies. Third-party strong-named packages (Newtonsoft.Json and
   similar) are left untouched and shared between builds. Revit, Autodesk and .NET assemblies are never
   renamed.
3. **Load.** The renamed main assembly is loaded with `Assembly.LoadFrom`. Its `Location` is the shadow copy,
   so ribbon buttons created with `GetExecutingAssembly().Location` point at the right build.
4. **Original names resolve to the newest build.** An `AssemblyResolve` handler answers requests for
   `OurApp`, from XAML/BAML, `Type.GetType` or serializers, with the newest generation. WPF's resource cache
   is cleared on reload.
5. **Ribbon clean-up.** Before an application starts, the ribbon (AdWindows) and Revit's internal panel
   registries are recorded. What the application adds is removed again before the next build starts, so
   `CreateRibbonTab` / `CreateRibbonPanel` with the same names succeed again.

Each build stays in memory until Revit closes. That's fine for a development session; restart Revit
once in a while.

## Repository layout

```
src/AddinLoader/          the Revit add-in
  App.cs                  ribbon + startup
  Commands/               Application Loader, Faceless, Console, Reload All
  Core/                   reload engine (ShadowLoader, AssemblyRewriter, RibbonTracker, AddinLoaderService…)
  ConsolePane/            dockable Console pane + Console/Debug capture
  UI/                     Application Loader window, icons
samples/SampleAddin/      add-in with a ribbon tab, a command and a XAML window, for trying reloads
tests/AddinLoader.Tests/  tests of the reload engine (run anywhere: dotnet test tests/AddinLoader.Tests)
install.ps1               build + install for given Revit versions
```

Build for a specific Revit version: `dotnet build src/AddinLoader -c Release -p:RevitVersion=2025`.
