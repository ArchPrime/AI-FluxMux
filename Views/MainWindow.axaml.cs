using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.ViewModels;

namespace FluxMux.Avalonia.Views;

public partial class MainWindow : Window
{
    private ScaleTransform? _uiScaleTransform;
    private string? _draggedPriorityName;
    private string? _draggedPriorityCollection;
    private bool _exitCleanupStarted;
    private InterventionPopupWindow? _interventionPopup;
    private bool _interventionPopupClosing;
    private readonly List<HelpTopicEntry> _helpTopics = new();
    private readonly List<FileSystemWatcher> _helpWatchers = new();
    private bool _helpIndexUpdating;
    private string? _loadedHelpPath;
    private DateTime _loadedHelpUtc;
    private Timer? _helpReloadTimer;

    static MainWindow()
    {
        // Class handlers fire before instance handlers, so these stop scroll-into-view
        // from jumping the page when a ListBox row is selected or a control is focused.
        RequestBringIntoViewEvent.AddClassHandler<ScrollViewer>((_, e) => e.Handled = true);
        RequestBringIntoViewEvent.AddClassHandler<ListBoxItem>((_, e) => e.Handled = true);
        InputElement.PointerPressedEvent.AddClassHandler<Button>(OnHeaderLockButtonPointerPressed, handledEventsToo: true);
    }

    public MainWindow()
    {
        InitializeComponent();
        HelpUiNotes.EnsureFromDisk();
        _uiScaleTransform = this.FindControl<LayoutTransformControl>("LayoutScaler")
            ?.LayoutTransform as ScaleTransform;

        DataContextChanged += OnDataContextChanged;
        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
        AddHandler(PointerPressedEvent, OnUserInterfaceActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnUserInterfaceActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        var profileScroll = this.FindControl<ScrollViewer>("ModelProfilesScroll");
        profileScroll?.AddHandler(
            Control.RequestBringIntoViewEvent,
            (_, e) => e.Handled = true,
            RoutingStrategies.Bubble | RoutingStrategies.Tunnel,
            handledEventsToo: true);
        var chooser = this.FindControl<ComboBox>("RecoveryReplacementChooser");
        if (chooser is not null)
        {
            chooser.DropDownOpened += (_, _) =>
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.RecoveryChooserDropDownOpen = true;
                }
            };
            chooser.DropDownClosed += (_, _) =>
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.RecoveryChooserDropDownOpen = false;
                }
            };
        }
    }

    private void OnUserInterfaceActivity(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.NoteUserInterfaceActivity();
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (_exitCleanupStarted)
        {
            return;
        }

        e.Cancel = true;
        _exitCleanupStarted = true;
        CloseInterventionPopup();
        Hide();
        _ = FinishExitCleanupAsync(vm);
    }

    private async Task FinishExitCleanupAsync(MainViewModel vm)
    {
        try
        {
            var cleanup = Task.Run(vm.ShutdownManagedRuntimesForExit);
            await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(8)));
        }
        catch
        {
        }

        StopHelpFileWatchers();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
                return;
            }

            Close();
        });
        Environment.Exit(0);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        vm.ApplySavedWindowPreferences();

        ApplySavedGeometry(vm);

        ApplyScale(vm.UiScaleFactor);
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.ProfileExpanderRevealRequested += OnProfileExpanderRevealRequested;

        PositionChanged += (_, _) => SaveGeometry(vm);
        SizeChanged   += (_, _) => SaveGeometry(vm);
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        StartupCrashLog.MarkWindowShown();
        if (DataContext is MainViewModel vm)
        {
            ApplySavedGeometry(vm);
            SyncInterventionPopup();
        }
    }

    private void ApplySavedGeometry(MainViewModel vm)
    {
        var (x, y, w, h) = vm.LoadWindowGeometry();
        Width = w;
        Height = h;
        if (x == int.MinValue || y == int.MinValue)
        {
            return;
        }

        var working = Screens.All
            .Select(screen => new ScreenBox(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height))
            .ToList();
        var proposed = new ScreenBox(x, y, (int)w, (int)h);
        if (WindowPlacement.HasUsableOnScreenArea(proposed, working))
        {
            Position = new PixelPoint(x, y);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.UiScaleFactor) && sender is MainViewModel vm)
        {
            ApplyScale(vm.UiScaleFactor);
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedUiTheme))
        {
            ApplyThemeToOwnedOverlays();
        }

        if (e.PropertyName is nameof(MainViewModel.InterventionPopupsEnabled)
            or nameof(MainViewModel.CloudRecommendVisible)
            or nameof(MainViewModel.LocalReloadRecommendVisible))
        {
            SyncInterventionPopup();
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedMainTabIndex) && IsHelpTabSelected())
        {
            ReloadHelpFromFile(force: false);
        }

        if (e.PropertyName == nameof(MainViewModel.DiagnosticTerminalText))
        {
            Dispatcher.UIThread.Post(() =>
            {
                var terminal = this.FindControl<TextBox>("DiagnosticTerminal");
                if (terminal is null)
                {
                    return;
                }

                if (terminal.IsFocused)
                {
                    terminal.CaretIndex = terminal.Text?.Length ?? 0;
                    return;
                }

                var inner = terminal.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (inner is not null)
                {
                    inner.Offset = new Vector(inner.Offset.X, Math.Max(0, inner.Extent.Height - inner.Viewport.Height));
                }
            });
        }
    }

    private void SyncInterventionPopup()
    {
        if (DataContext is not MainViewModel vm || _exitCleanupStarted)
        {
            return;
        }

        var needsDecision = vm.InterventionPopupsEnabled
            && (vm.CloudRecommendVisible || vm.LocalReloadRecommendVisible);
        if (!needsDecision)
        {
            Dispatcher.UIThread.Post(CloseInterventionPopupIfStillIdle, DispatcherPriority.ContextIdle);
            return;
        }

        if (_interventionPopup is null)
        {
            _interventionPopup = new InterventionPopupWindow
            {
                DataContext = vm,
                Topmost = true,
                ShowInTaskbar = true,
                RequestedThemeVariant = Application.Current?.RequestedThemeVariant
            };
            _interventionPopup.Closing += OnInterventionPopupClosing;
            _interventionPopup.Closed += OnInterventionPopupClosed;
            _interventionPopup.Show();
        }
    }

    private void OnInterventionPopupClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is Window popup)
        {
            popup.Topmost = false;
        }
    }

    private void OnInterventionPopupClosed(object? sender, EventArgs e)
    {
        if (_interventionPopupClosing)
        {
            return;
        }

        if (_interventionPopup is not null)
        {
            _interventionPopup.Closing -= OnInterventionPopupClosing;
            _interventionPopup.Closed -= OnInterventionPopupClosed;
            _interventionPopup = null;
        }
    }

    private void CloseInterventionPopupIfStillIdle()
    {
        if (DataContext is MainViewModel vm
            && vm.InterventionPopupsEnabled
            && (vm.CloudRecommendVisible || vm.LocalReloadRecommendVisible))
        {
            return;
        }

        CloseInterventionPopup();
    }

    private void CloseInterventionPopup()
    {
        var popup = _interventionPopup;
        if (popup is null)
        {
            return;
        }

        _interventionPopup = null;
        _interventionPopupClosing = true;
        popup.Closing -= OnInterventionPopupClosing;
        popup.Closed -= OnInterventionPopupClosed;
        try
        {
            popup.Topmost = false;
            popup.Hide();
        }
        catch
        {
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                popup.Close();
            }
            catch
            {
            }
            finally
            {
                _interventionPopupClosing = false;
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void OnProfileExpanderRevealRequested(CloudVariantTreeItemViewModel item, bool focusNameBox)
        => ProfileExpanderReveal.Schedule(this, item, focusNameBox);

    private void GpuVramButton_Click(object? sender, RoutedEventArgs e)
    {
        if (GpuVramPopup is not null && !GpuVramPopup.IsOpen)
        {
            GpuVramPopup.IsOpen = true;
        }
    }

    private void GpuVramPopup_Opened(object? sender, EventArgs e)
    {
        ApplyThemeToOwnedOverlays();
        if (DataContext is MainViewModel vm)
        {
            _ = vm.LoadGpuVramFlyoutCommand.ExecuteAsync(null);
        }
    }

    private void ApplyThemeToOwnedOverlays()
    {
        var variant = Application.Current?.RequestedThemeVariant;
        if (_interventionPopup is not null)
        {
            _interventionPopup.RequestedThemeVariant = variant;
        }

        if (GpuVramThemeScope is not null)
        {
            GpuVramThemeScope.RequestedThemeVariant = variant;
        }
    }

    private void GpuVramPopupClose_Click(object? sender, RoutedEventArgs e)
    {
        if (GpuVramPopup is not null)
        {
            GpuVramPopup.IsOpen = false;
        }
    }

    private void ApplyScale(double factor)
    {
        if (_uiScaleTransform is null)
            return;
        _uiScaleTransform.ScaleX = factor;
        _uiScaleTransform.ScaleY = factor;
    }

    private void HeaderChrome_LayoutUpdated(object? sender, EventArgs e)
    {
        var spacer = this.FindControl<Control>("CaptionButtonSpacer");
        if (spacer is null)
        {
            return;
        }

        var right = WindowDecorationMargin.Right;
        spacer.Width = right > 1 ? right : 138;
    }

    private void SaveGeometry(MainViewModel vm)
    {
        if (WindowState != WindowState.Normal)
            return;
        vm.SaveWindowGeometry(Position.X, Position.Y, (int)Width, (int)Height);
    }

    private static void OnHeaderLockButtonPointerPressed(Button button, PointerPressedEventArgs e)
    {
        if (!button.Classes.Contains("headerLockButton")
            || !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        var profile = button.DataContext as CloudVariantTreeItemViewModel;
        if (profile is null)
        {
            for (Visual? visual = button; visual is not null; visual = visual.GetVisualParent())
            {
                if (visual is Expander expander)
                {
                    profile = expander.DataContext as CloudVariantTreeItemViewModel;
                    break;
                }

                if (visual is StyledElement { DataContext: CloudVariantTreeItemViewModel fromAncestor })
                {
                    profile = fromAncestor;
                    break;
                }
            }
        }

        if (profile is null)
        {
            return;
        }

        var window = TopLevel.GetTopLevel(button) as MainWindow
                     ?? button.FindAncestorOfType<MainWindow>();
        if (window?.DataContext is not MainViewModel vm)
        {
            return;
        }

        if (vm.ToggleProfileDefaultLockCommand is IAsyncRelayCommand asyncCommand)
        {
            _ = asyncCommand.ExecuteAsync(profile);
            return;
        }

        vm.ToggleProfileDefaultLockCommand.Execute(profile);
    }

    private void VariantExpander_Expanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: CloudVariantTreeItemViewModel item } ||
            DataContext is not MainViewModel vm)
            return;

        vm.OnAdvancedProfileExpanderExpanded(item);
        if (!vm.SuppressProfileExpanderReveal)
        {
            ProfileExpanderReveal.Schedule(this, item, focusNameBox: false);
        }
    }

    private void VariantExpander_Collapsing(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander)
        {
            return;
        }

        if (ProfileExpanderReveal.TryDeferCollapse(expander))
        {
            e.Handled = true;
            if (e is CancelRoutedEventArgs cancel)
            {
                cancel.Cancel = true;
            }
        }
    }

    private void SuppressProfileListSelection(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void PriorityListBox_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox listBox || !e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed)
            return;

        var selectedPriority = GetPriorityAtPointer(listBox, e);
        if (selectedPriority is null)
            return;

        listBox.SelectedItem = selectedPriority;
        _draggedPriorityName = selectedPriority;
        _draggedPriorityCollection = listBox.ItemsSource == (DataContext as MainViewModel)?.CloudPriorityOrder ? "cloud" : "local";
        e.Pointer.Capture(listBox);
        e.Handled = true;
    }

    private void PriorityListBox_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not ListBox listBox || DataContext is not MainViewModel vm ||
            string.IsNullOrWhiteSpace(_draggedPriorityName) || !e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed)
            return;

        var targetPriority = GetPriorityAtPointer(listBox, e);
        if (string.IsNullOrWhiteSpace(targetPriority) || targetPriority == _draggedPriorityName)
            return;

        if (_draggedPriorityCollection == "cloud")
            vm.ReorderCloudPriority(_draggedPriorityName, targetPriority);
        else
            vm.ReorderLocalPriority(_draggedPriorityName, targetPriority);

        e.Handled = true;
    }

    private void PriorityListBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        _draggedPriorityName = null;
        _draggedPriorityCollection = null;
        e.Handled = true;
    }

    private static string? GetPriorityAtPointer(ListBox listBox, PointerEventArgs e)
    {
        for (var index = 0; index < listBox.ItemCount; index++)
        {
            if (listBox.ContainerFromIndex(index) is not ListBoxItem item)
                continue;

            var point = e.GetPosition(item);
            if (new Rect(item.Bounds.Size).Contains(point))
                return listBox.Items[index] as string;
        }

        return null;
    }

    private void OnHelpChromeLoaded(object? sender, RoutedEventArgs e)
    {
        ReloadHelpFromFile(force: true);
        StartHelpFileWatchers();

        // Help colours are read from the theme as the page is built, so a light or dark
        // switch has to draw the page again.
        ActualThemeVariantChanged -= OnHelpThemeVariantChanged;
        ActualThemeVariantChanged += OnHelpThemeVariantChanged;
    }

    private void OnHelpThemeVariantChanged(object? sender, EventArgs e)
        => ReloadHelpFromFile(force: true);

    private void OnMainTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // TabControl raises SelectionChanged while XAML is still loading, before this
        // window has a name scope. FindControl throws then and the app never appears.
        if (!IsInitialized)
        {
            return;
        }

        if (IsHelpTabSelected(sender as TabControl))
        {
            ReloadHelpFromFile(force: false);
        }
    }

    private bool IsHelpTabSelected(TabControl? tabs = null)
    {
        tabs ??= TryFindNamed<TabControl>("MainTabs");
        return tabs?.SelectedItem is TabItem item
            && string.Equals(Convert.ToString(item.Header), "Help", StringComparison.Ordinal);
    }

    private T? TryFindNamed<T>(string name) where T : Control
    {
        try
        {
            return this.FindControl<T>(name);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void ReloadHelpFromFile(bool force)
    {
        var panel = TryFindNamed<StackPanel>("HelpBodyPanel");
        if (panel is null)
        {
            return;
        }

        var path = HelpHtmlFile.FindNewest();
        if (path is null)
        {
            _loadedHelpPath = null;
            _loadedHelpUtc = DateTime.MinValue;
            HelpHtmlViewBuilder.ShowMessage(
                panel,
                "Help.html was not found next to AI-FluxMux or in the folder you started it from. Save Help.html there (Web Page, Filtered), then return to this tab.");
            RebuildHelpIndex();
            ApplyHelpFilter();
            return;
        }

        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex)
        {
            HelpHtmlViewBuilder.ShowMessage(panel, "Could not read Help.html: " + ex.Message);
            RebuildHelpIndex();
            ApplyHelpFilter();
            return;
        }

        if (!force
            && _loadedHelpPath is not null
            && path.Equals(_loadedHelpPath, StringComparison.OrdinalIgnoreCase)
            && stamp == _loadedHelpUtc)
        {
            return;
        }

        var selectedTitle = (this.FindControl<ListBox>("HelpIndexList")?.SelectedItem as HelpTopicEntry)?.Title;
        HelpDocument document;
        try
        {
            document = LoadHelpDocument(path);
        }
        catch (Exception ex)
        {
            HelpHtmlViewBuilder.ShowMessage(panel, "Could not read Help.html: " + ex.Message);
            _loadedHelpPath = path;
            _loadedHelpUtc = stamp;
            RebuildHelpIndex();
            ApplyHelpFilter();
            return;
        }

        HelpHtmlViewBuilder.Populate(panel, document, OpenHelpLink);
        HelpUiNotes.Publish(document);
        _loadedHelpPath = path;
        _loadedHelpUtc = stamp;
        RebuildHelpIndex();
        ApplyHelpFilter();
        RestoreHelpSelection(selectedTitle);
        StartHelpFileWatchers();
    }

    private static HelpDocument LoadHelpDocument(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                return HelpHtmlParser.Load(path);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(80);
            }
        }

        return HelpHtmlParser.Load(path);
    }

    private void OpenHelpLink(string url)
    {
        // A cross-reference inside Help.html jumps to that topic rather than handing an
        // anchor to the browser, which would leave the tab and land nowhere.
        if (url.StartsWith('#') && TryGoToHelpTopic(url[1..]))
        {
            return;
        }

        if (DataContext is MainViewModel vm)
        {
            vm.OpenExternalLinkCommand.Execute(url);
        }
    }

    /// <summary>
    /// Follows a link written in Help.html as <c>href="#Topic title"</c>.
    /// </summary>
    private bool TryGoToHelpTopic(string reference)
    {
        string wanted;
        try
        {
            wanted = Uri.UnescapeDataString(reference).Trim();
        }
        catch (UriFormatException)
        {
            wanted = reference.Trim();
        }

        if (wanted.Length == 0)
        {
            return false;
        }

        var match = HelpTopicSearch.FindByReference(_helpTopics, wanted);
        if (match?.Target is null)
        {
            return false;
        }

        // The body is never filtered, so the topic can be scrolled to even when a search
        // has taken it out of the index.
        var list = this.FindControl<ListBox>("HelpIndexList");
        if (list is not null && list.Items.Contains(match))
        {
            _helpIndexUpdating = true;
            try
            {
                list.SelectedItem = match;
            }
            finally
            {
                _helpIndexUpdating = false;
            }
        }

        ScrollHelpTopicIntoView(match.Target);
        return true;
    }

    private void RestoreHelpSelection(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var list = this.FindControl<ListBox>("HelpIndexList");
        if (list is null)
        {
            return;
        }

        var match = _helpTopics.FirstOrDefault(topic =>
            topic.Title.Equals(title, StringComparison.Ordinal));
        if (match is null)
        {
            return;
        }

        _helpIndexUpdating = true;
        try
        {
            list.SelectedItem = match;
        }
        finally
        {
            _helpIndexUpdating = false;
        }
    }

    private void StartHelpFileWatchers()
    {
        var directories = HelpHtmlFile.CandidatePaths()
            .Select(Path.GetDirectoryName)
            .Where(dir => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            .Select(dir => Path.GetFullPath(dir!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var watching = new HashSet<string>(
            _helpWatchers.Select(watcher => watcher.Path),
            StringComparer.OrdinalIgnoreCase);
        if (watching.SetEquals(directories))
        {
            return;
        }

        StopHelpFileWatchers();
        foreach (var dir in directories)
        {
            FileSystemWatcher watcher;
            try
            {
                watcher = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size
                        | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
            }
            catch (Exception)
            {
                continue;
            }

            watcher.Changed += OnHelpFileDiskEvent;
            watcher.Created += OnHelpFileDiskEvent;
            watcher.Deleted += OnHelpFileDiskEvent;
            watcher.Renamed += OnHelpFileDiskEvent;
            _helpWatchers.Add(watcher);
        }
    }

    private void OnHelpFileDiskEvent(object sender, FileSystemEventArgs e)
    {
        if (!HelpHtmlFile.IsHelpFileName(e.Name) && !HelpHtmlFile.IsHelpFileName(e.FullPath))
        {
            return;
        }

        _helpReloadTimer ??= new Timer(_ =>
        {
            Dispatcher.UIThread.Post(() => ReloadHelpFromFile(force: true));
        });
        _helpReloadTimer.Change(500, Timeout.Infinite);
    }

    private void StopHelpFileWatchers()
    {
        _helpReloadTimer?.Dispose();
        _helpReloadTimer = null;
        foreach (var watcher in _helpWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnHelpFileDiskEvent;
            watcher.Created -= OnHelpFileDiskEvent;
            watcher.Deleted -= OnHelpFileDiskEvent;
            watcher.Renamed -= OnHelpFileDiskEvent;
            watcher.Dispose();
        }

        _helpWatchers.Clear();
    }

    private void OnHelpSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyHelpFilter();
    }

    private void OnHelpSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        var list = this.FindControl<ListBox>("HelpIndexList");
        if (list is null || list.ItemCount == 0)
        {
            return;
        }

        // Index 0 can be a section banner, which is a label rather than a topic.
        var first = list.ItemsSource?
            .OfType<HelpTopicEntry>()
            .FirstOrDefault(entry => entry.IsTopic);
        if (first is null)
        {
            return;
        }

        list.SelectedItem = first;
        ScrollHelpTopicIntoView(first.Target);
        e.Handled = true;
    }

    private void OnHelpIndexSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_helpIndexUpdating)
        {
            return;
        }

        var list = this.FindControl<ListBox>("HelpIndexList");
        if (list?.SelectedItem is HelpTopicEntry { IsTopic: true } entry)
        {
            ScrollHelpTopicIntoView(entry.Target);
        }
    }

    private void RebuildHelpIndex()
    {
        _helpTopics.Clear();
        var panel = this.FindControl<StackPanel>("HelpBodyPanel");
        if (panel is null)
        {
            return;
        }

        // Children are in document order, so a Heading 1 banner names every topic
        // that follows it until the next banner.
        var section = string.Empty;
        foreach (var child in panel.Children)
        {
            if (child is TextBlock banner && banner.Classes.Contains("helpSection"))
            {
                section = banner.Text ?? string.Empty;
                continue;
            }

            if (child is not StackPanel topic || !topic.Classes.Contains("helpTopic"))
            {
                continue;
            }

            var title = topic.Tag as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var body = new StringBuilder();
            CollectHelpText(topic, body);
            _helpTopics.Add(new HelpTopicEntry
            {
                Title = title,
                Target = topic,
                SearchText = body.ToString(),
                Section = section
            });
        }
    }

    private void ApplyHelpFilter()
    {
        var list = this.FindControl<ListBox>("HelpIndexList");
        var status = this.FindControl<TextBlock>("HelpSearchStatus");
        var search = this.FindControl<TextBox>("HelpSearchBox");
        if (list is null)
        {
            return;
        }

        var query = search?.Text ?? string.Empty;
        var matches = _helpTopics
            .Where(topic => HelpTopicSearch.Matches(topic.SearchText, query))
            .ToList();
        var display = HelpTopicSearch.GroupBySection(matches);

        _helpIndexUpdating = true;
        try
        {
            var keep = list.SelectedItem as HelpTopicEntry;
            list.ItemsSource = display;
            list.SelectedItem = keep is not null && display.Contains(keep) ? keep : null;
        }
        finally
        {
            _helpIndexUpdating = false;
        }

        if (status is null)
        {
            return;
        }

        if (matches.Count == 0)
        {
            status.Text = "No matching topics.";
            return;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            status.Text = matches.Count == 1 ? "1 topic." : matches.Count + " topics.";
            return;
        }

        status.Text = matches.Count == 1 ? "1 matching topic." : matches.Count + " matching topics.";
    }

    private void ScrollHelpTopicIntoView(Control? target)
    {
        if (target is null)
        {
            return;
        }

        void Scroll()
        {
            var scroll = this.FindControl<ScrollViewer>("HelpBodyScroll");
            if (scroll is null)
            {
                return;
            }

            var point = target.TranslatePoint(new Point(0, 0), scroll);
            if (point is { } offset)
            {
                scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + offset.Y - 12));
            }
        }

        Dispatcher.UIThread.Post(Scroll, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(Scroll, DispatcherPriority.Background);
    }

    private static void CollectHelpText(Control node, StringBuilder text)
    {
        if (node is TextBlock textBlock)
        {
            if (!string.IsNullOrWhiteSpace(textBlock.Text))
            {
                Append(text, textBlock.Text);
            }
            else if (textBlock.Inlines is { Count: > 0 } inlines)
            {
                // A paragraph carrying a control name, a path, or a link is built from
                // inlines and leaves Text empty, so most of Help would not be searchable.
                foreach (var inline in inlines)
                {
                    if (inline is Run run)
                    {
                        Append(text, run.Text);
                    }
                }
            }
        }
        else if (node is Button button && button.Content is string content)
        {
            Append(text, content);
        }

        if (node is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Control control)
                {
                    CollectHelpText(control, text);
                }
            }

            return;
        }

        if (node is Decorator decorator && decorator.Child is Control decorated)
        {
            CollectHelpText(decorated, text);
        }
    }

    /// <summary>
    /// Drops the spacing characters the Help view adds around runs, so a search for a
    /// path or a control name is not broken by them.
    /// </summary>
    private static void Append(StringBuilder text, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var ch in value)
        {
            if (ch is not ('\u200B' or '\u2009'))
            {
                text.Append(ch);
            }
        }

        text.Append(' ');
    }
}