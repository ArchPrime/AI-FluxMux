# FluxMux.Avalonia - Build & Dev Brief

## Critical: Avalonia CheckBox Events

**DO NOT use `Checked`/`Unchecked` event handlers in XAML for `CheckBox`.**

Avalonia's `CheckBox` control does NOT support `Checked`/`Unchecked` as XAML properties. This causes build errors:
```
Avalonia error AVLN2000: Unable to resolve suitable regular or attached property Checked on type Avalonia.Controls:Avalonia.Controls.CheckBox
```

**Correct approach:**
- Use `Click="HandlerName"` in XAML
- In the code-behind handler, check `checkBox.IsChecked` to determine the new state
- Example:
  ```xml
  <CheckBox Click="VramProcessGpuToggle_Click" Tag="{Binding NameLabel}">
  ```
  ```csharp
  private void VramProcessGpuToggle_Click(object? sender, RoutedEventArgs e)
  {
      if (sender is not CheckBox checkBox || checkBox.Tag is not string appName) return;
      bool isChecked = checkBox.IsChecked == true;
      // Handle based on isChecked
  }
  ```

## Critical: GpuKindConverters Namespace

**The `GpuKindConverters` class is in `FluxMux.Avalonia.ViewModels`, NOT `Avalonia.Data.Converters`.**

In XAML, use `vm:GpuKindConverters`, NOT `conv:GpuKindConverters`:
```xml
<!-- CORRECT -->
ItemsSource="{Binding VramProcessRows, Converter={x:Static vm:GpuKindConverters.DiscreteVramFilter}}"

<!-- WRONG - causes build error -->
ItemsSource="{Binding VramProcessRows, Converter={x:Static conv:GpuKindConverters.DiscreteVramFilter}}"
```

## Critical: .NET SDK Detection in start-ai-fluxmux.cmd

**The script must check BOTH PATH and the common installation location.**

If `dotnet.exe` is not on PATH, check `C:\Program Files\dotnet\dotnet.exe`:
```batch
set "DOTNET_EXE="
for /f "delims=" %%D in ('where dotnet.exe 2^>nul') do (
    if not defined DOTNET_EXE set "DOTNET_EXE=%%D"
)
rem If not found on PATH, check the common installation location
if not defined DOTNET_EXE (
    if exist "C:\Program Files\dotnet\dotnet.exe" set "DOTNET_EXE=C:\Program Files\dotnet\dotnet.exe"
)
```

**Also:** Use `EnableDelayedExpansion` and `!VAR!` syntax for variables set inside loops/blocks.

**Label flow:** Use a `:dotnet_found` label to skip past the error section. Do NOT let the script fall through from `:dotnet_ok` to `:dotnet_error`.

## Build & Test

**To build:**
```
cd c:\AI_Workbench\Workspace\FluxMux.Avalonia
dotnet build --no-restore -v q
```

**To run tests (without killing the running app):**
```
dotnet build FluxMux.Avalonia.Tests\FluxMux.Avalonia.Tests.csproj -c Debug --nologo -v q ^
  -p:BaseOutputPath=artifacts\testrun\ -p:BaseIntermediateOutputPath=artifacts\testrun\obj\
dotnet test FluxMux.Avalonia.Tests\FluxMux.Avalonia.Tests.csproj -c Debug --no-build --nologo ^
  -p:BaseOutputPath=artifacts\testrun\ -p:BaseIntermediateOutputPath=artifacts\testrun\obj\ ^
  --filter "FullyQualifiedName~<TestClassName>"
```

**NEVER close the running app to build/test.** It severs the chat. Use separate output dirs as shown above.

## Backup

**To back up the dev source + built output:**
```
cd c:\AI_Workbench\Workspace
backup-fluxmux-dev.bat
```

This creates `backups\FluxMux.Avalonia_{version}_{timestamp}_source` with:
1. Source code (excludes bin, obj, artifacts, .tmp-sessions, backup, master)
2. Built output (bin\Debug\net10.0, excludes non-Windows runtimes)
3. Git diff, status, and recent log

The backup is self-contained and can be run directly (if .NET 10 SDK is installed).

## Key Files

- `Views\MainWindow.axaml` - Main window XAML (large file, ~4000+ lines)
- `Views\MainWindow.axaml.cs` - Code-behind for main window
- `ViewModels\MainViewModel.cs` - Main ViewModel (large file, ~7000+ lines)
- `ViewModels\GpuKindConverters.cs` - Value converters for GPU kind filtering
- `Services\WindowsGpuPreference.cs` - Windows GPU affinity management
- `start-ai-fluxmux.cmd` - Build & run script
- `backup-fluxmux-dev.bat` - Backup script

## Common Pitfalls

1. **Don't use WPF-style event handlers in Avalonia XAML** - Use `Click` instead of `Checked`/`Unchecked`
2. **Don't assume namespaces** - Always verify the namespace of a class before using it in XAML
3. **Don't rely on PATH alone** - Check common installation locations as a fallback
4. **Don't close the running app** - Use separate output dirs for builds/tests
5. **Don't use the `AvoidLockedAppHost` escape hatch** for new changes - It references stale prebuilt DLLs
