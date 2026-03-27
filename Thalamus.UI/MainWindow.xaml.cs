using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Thalamus.Core;
using Thalamus.Nucleus.BridgeMod;
using Thalamus.Nucleus.Kanon;
using Thalamus.UI.ViewModels;

namespace Thalamus.UI;

public partial class MainWindow : Window
{
    // ── ViewModel ────────────────────────────────────────────────────
    private MainViewModel _vm = null!;

    // ── Gold: Recovery file path (auto-save every 2 min) ──────────────
    private static readonly string RecoveryFilePath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "recovery.json");

    // ── Visual lookup maps ───────────────────────────────────────────
    /// <summary>NodeId → card Border on the canvas.</summary>
    private readonly Dictionary<Guid, FrameworkElement> _cardMap = [];

    /// <summary>(NodeId, PortName, IsOutput) → Ellipse dot.</summary>
    private readonly Dictionary<(Guid, string, bool), Ellipse> _portDotMap = [];

    /// <summary>SynapseId → Bezier Path on the canvas.</summary>
    private readonly Dictionary<Guid, Path> _wireMap = [];

    // ── Execution state (Phase 6) ─────────────────────────────────────
    /// <summary>(NodeId, PortName, IsOutput) → value bubble Border shown next to each port.</summary>
    private readonly Dictionary<(Guid, string, bool), Border> _portValueMap = [];

    /// <summary>NodeId → card border SolidColorBrush (mutable for ColorAnimation).</summary>
    private readonly Dictionary<Guid, SolidColorBrush> _cardBorderBrushMap = [];

    // ── Phase 11: Live Value Overlay ──────────────────────────────────────
    /// <summary>True when the graph has changed since the last Pulse — bubbles are stale.</summary>
    private bool _graphIsStale = false;

    /// <summary>SynapseId → transparent hit-area Path for wire hover tooltips.</summary>
    private readonly Dictionary<Guid, Path> _wireHitMap = [];

    /// <summary>Last Pulse results — used to populate wire tooltips.</summary>
    private Dictionary<(Guid, string, bool), DataPacket>? _lastPulseResults;

    // ── Wire colors ──────────────────────────────────────────────────
    private static readonly Brush WireNormalBrush =
        new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));  // High-contrast white
    private static readonly Brush WireInvalidBrush = Brushes.Red;
    private static readonly Brush WireActiveBrush =
        new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));  // Pulse cyan — NOT frozen

    // ── Pan state ────────────────────────────────────────────────────
    private bool   _isPanning;
    private Point  _panStart;
    private double _panOriginX;
    private double _panOriginY;

    // ── Node-drag state ──────────────────────────────────────────────
    private bool   _isDraggingNode;
    private Guid   _dragNodeId;
    private Point  _dragNodeStart;    // mouse pos in GraphCanvas coords at drag begin
    private double _dragNodeOriginX;  // Canvas.GetLeft of card at drag begin
    private double _dragNodeOriginY;  // Canvas.GetTop  of card at drag begin

    // ── Wire-drag state ──────────────────────────────────────────────
    private bool   _isWiring;
    private Guid   _wireSourceNodeId;
    private string _wireSourcePortName  = string.Empty;
    private string _wireSourcePortType  = string.Empty;
    private Point  _wireSourceCanvasPos; // center of source dot in GraphCanvas coords
    private Path?  _ghostWire;

    // ── Persistence state (Phase 5) ──────────────────────────────────
    private string? _currentFilePath;
    private Dictionary<(string, string), NodeSchema> _paletteIndex = [];

    // ── Sovereign license state ─────────────────────────────────────
    private const string LockedTooltip =
        "Sovereign Tier Required. Visit rootedresilientshop.com to unlock.";
    private const string SentryKeyFileName = "sentry.key";
    private const string PulseAuthFileName = "pulse.auth";
    private const string DevModeFileName = "dev.mode";
    private const string ConfigFileName = "config.json";

    private static readonly string DataDirectory =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Data");
    private static readonly string SentryKeyPath =
        System.IO.Path.Combine(DataDirectory, SentryKeyFileName);
    private static readonly string PulseAuthPath =
        System.IO.Path.Combine(DataDirectory, PulseAuthFileName);
    private static readonly string DevModePath =
        System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", DevModeFileName));
    private static readonly string ConfigPath =
        System.IO.Path.Combine(DataDirectory, ConfigFileName);
    private static readonly string TemplatesPath =
        System.IO.Path.Combine(DataDirectory, "Templates");

    private bool _sentryUnlocked;
    private bool _pulseUnlocked;
    private bool _devMode;
    private Popup? _onboardingPopup;
    private int _onboardingStep;
    private readonly Dictionary<MenuItem, string> _menuHeaders = [];
    private object? _liveSyncTooltip;
    private ContextMenu? _canvasContextMenu;
    private Brush? _menuPulseBaseBackground;

    // ────────────────────────────────────────────────────────────────
    public MainWindow()
    {
        System.IO.Directory.CreateDirectory(DataDirectory);
        try
        {
            var auditPath = System.IO.Path.Combine(DataDirectory, "kanon_audit.log");
            var line = $"Startup Diagnostics: {DateTime.Now:O}{Environment.NewLine}";
            System.IO.File.AppendAllText(auditPath, line);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.ToString(),
                "Thalamus — Diagnostics",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        InitializeComponent();
        this.Visibility = Visibility.Visible;
        GraphCanvas.Visibility = Visibility.Visible;
        this.DataContext = this;
        Panel.SetZIndex(CanvasBorder, 10);
        Panel.SetZIndex(GraphCanvas, 11);
        _menuPulseBaseBackground = MenuPulse.Background;

        if (System.IO.File.Exists(DevModePath))
        {
            _devMode = true;
            MenuFileRoot.IsEnabled = true;
            MenuPulse.IsEnabled = true;
            MenuGraphRoot.IsEnabled = true;
            foreach (var obj in MenuFileRoot.Items)
            {
                if (obj is MenuItem menuItem)
                    menuItem.IsEnabled = true;
            }
            CommandManager.InvalidateRequerySuggested();
        }

        LoadProviders();

        // Freeze the static brushes to avoid thread-affinity cost on repeated use
        WireNormalBrush.Freeze();
        WireInvalidBrush.Freeze();
    }

    // ── Provider loading (unchanged seam from Phase 1) ───────────────
    private void LoadProviders()
    {
        var providers = new List<INucleusProvider>
        {
            new BridgeModProvider(),
            new KanonProvider()
        };
        _vm = new MainViewModel(providers);
        PaletteList.ItemsSource = _vm.PaletteViewSource.View;

        // Trial: Subscribe to node limit reached event
        _vm.TrialLimitReached += () => new TrialPopupWindow { Owner = this }.ShowDialog();

        // Build palette index: (ProviderName, SchemaName) -> NodeSchema
        foreach (var schema in _vm.Palette)
        {
            if (schema.ProviderName != null)
                _paletteIndex[(schema.ProviderName, schema.Name)] = schema;
        }

        PaletteList.ItemContainerGenerator.StatusChanged += (_, _) => ApplyLicenseStateToPalette();

        // Wire menu items to handlers
        MenuFileNew.Click += FileNew_Click;
        MenuFileOpen.Click += FileOpen_Click;
        MenuFileSave.Click += FileSave_Click;
        MenuFileSaveAs.Click += FileSaveAs_Click;
        MenuPulse.Click += (_, _) => TogglePulse();
        MenuHelpAbout.Click += (_, _) => new AboutWindow { Owner = this }.ShowDialog();
        MenuFileExportCs.Click += (_, _) =>
        {
            var code = _vm.ExportCSharp();
            new CodePreviewWindow(code) { Owner = this }.ShowDialog();
        };

        // Set up keyboard shortcuts (Ctrl+N/O/S)
        this.CommandBindings.Add(new CommandBinding(ApplicationCommands.New, FileNew_Click));
        this.CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, FileOpen_Click));
        this.CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, FileSave_Click));

        // Wire up menu items for Gold features
        MenuGraphAutoLayout.Click += (_, _) => AutoLayout();
        BuildCanvasContextMenu();

        // NEW: F5 shortcut for Pulse; Ctrl+L shortcut for Auto-Layout
        this.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5)
            {
                PulseGraph();
                e.Handled = true;
            }
            else if (e.Key == Key.L && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                AutoLayout();
                e.Handled = true;
            }
        };

        // Phase 12: RAM polling (every 2 seconds)
        var ramTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        ramTimer.Tick += (_, _) => UpdateRamDisplay();
        ramTimer.Start();
        UpdateRamDisplay();  // immediate initial read

        // Gold: Ghost auto-save every 2 minutes
        var autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        autoSaveTimer.Tick += (_, _) => AutoSaveRecovery();
        autoSaveTimer.Start();

        // Trial: 5-minute Live Sync session timer
        var syncSessionTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        syncSessionTimer.Tick += (_, _) =>
        {
            syncSessionTimer.Stop();
            if (_vm.IsProVersion) return;  // guard — upgraded mid-session
            LiveSyncToggle.IsChecked = false;
            StatusSyncText.Text = "Trial Sync Session ended. Re-toggle to resume or go Pro for unlimited sync.";
            StatusSyncText.Visibility = Visibility.Visible;
        };

        // Start timer when Live Sync is enabled; reset message
        LiveSyncToggle.Checked += (_, _) =>
        {
            StatusSyncText.Visibility = Visibility.Collapsed;
            if (!_vm.IsProVersion) syncSessionTimer.Start();
            LiveSyncToggle.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x6E, 0x1E));
            LiveSyncToggle.Foreground = Brushes.White;
        };

        // Stop timer when Live Sync is disabled manually
        LiveSyncToggle.Unchecked += (_, _) =>
        {
            syncSessionTimer.Stop();
            LiveSyncToggle.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x1A));
            LiveSyncToggle.Foreground = Brushes.White;
        };
        LiveSyncToggle.Click += (_, _) => ToggleLiveSync();

        // Sovereign: license state + onboarding (deferred until after render)
        this.Loaded += (_, _) =>
        {
            RefreshLicenseState();
            ApplyLicenseStateToUI();
            ShowOnboardingIfFirstRun();
            LoadNodeNexusTemplates();
        };

        // Gold: Check for unsaved recovery on startup (deferred until after render)
        this.Loaded += (_, _) => CheckRecovery();
    }

    // ── Category Accent Colors (Phase 7) ─────────────────────────────
    private static Color GetCategoryAccentColor(string category) => category switch
    {
        "Math"     => Color.FromRgb(0x2E, 0x5B, 0xFF),  // Deep Cobalt
        "Security" => Color.FromRgb(0x00, 0xC8, 0x53),  // Emerald Green
        _          => Color.FromRgb(0x55, 0x44, 0x99),  // Default Purple
    };

    // ── Phase 12: RAM Display Update ──────────────────────────────────
    private void UpdateRamDisplay()
    {
        long mb = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
        StatusRamText.Text = $"RAM: {mb} MB";
    }

    // ── Sovereign license state ─────────────────────────────────────
    private void RefreshLicenseState()
    {
        System.IO.Directory.CreateDirectory(DataDirectory);
        _sentryUnlocked = false;
        _pulseUnlocked = false;
        _devMode = System.IO.File.Exists(DevModePath);
        try
        {
            var auditPath = System.IO.Path.Combine(DataDirectory, "kanon_audit.log");
            var line = $"Checking for Dev Mode at: {DevModePath}{Environment.NewLine}";
            System.IO.File.AppendAllText(auditPath, line);
        }
        catch { }

        if (_devMode)
        {
            _sentryUnlocked = true;
            _pulseUnlocked = true;
            return;
        }

        if (System.IO.File.Exists(SentryKeyPath))
        {
            var seed = System.IO.File.ReadAllText(SentryKeyPath).Trim();
            if (!string.IsNullOrWhiteSpace(seed))
            {
                Environment.SetEnvironmentVariable("KANON_SEED", seed);
                _sentryUnlocked = true;
            }
        }

        if (System.IO.File.Exists(PulseAuthPath))
        {
            var auth = System.IO.File.ReadAllText(PulseAuthPath).Trim();
            if (!string.IsNullOrWhiteSpace(auth))
                _pulseUnlocked = true;
        }
    }

    private static string StatusLabel(bool unlocked) => unlocked ? "Active" : "Locked";

    private void UpdateSovereignStatusText()
    {
        StatusSentryText.Text = $"Sentry: {StatusLabel(_sentryUnlocked)}";
        StatusSentryText.Foreground = new SolidColorBrush(
            _sentryUnlocked ? Color.FromRgb(0x66, 0xCC, 0x66) : Color.FromRgb(0xCC, 0x66, 0x66));

        StatusPulseStateText.Text = $"Pulse: {StatusLabel(_pulseUnlocked)}";
        StatusPulseStateText.Foreground = new SolidColorBrush(
            _pulseUnlocked ? Color.FromRgb(0x66, 0xCC, 0x66) : Color.FromRgb(0xCC, 0x66, 0x66));
    }

    private bool IsSchemaLocked(NodeSchema schema)
    {
        if (_devMode) return false;
        return schema.ProviderName switch
        {
            "Kanon"     => !_sentryUnlocked,
            "BridgeMod" => !_pulseUnlocked,
            _           => false
        };
    }

    private void ApplyLicenseStateToUI()
    {
        UpdateSovereignStatusText();

        // Update sovereign module status dots
        BridgeModDot.Fill = _pulseUnlocked
            ? new SolidColorBrush(Color.FromRgb(0x44, 0xCC, 0x44))  // Green
            : new SolidColorBrush(Color.FromRgb(0xCC, 0x44, 0x44));  // Red
        KanonDot.Fill = _sentryUnlocked
            ? new SolidColorBrush(Color.FromRgb(0x44, 0xCC, 0x44))  // Green
            : new SolidColorBrush(Color.FromRgb(0xCC, 0x44, 0x44));  // Red

        ApplyLicenseStateToMenu();
        ApplyLicenseStateToPalette();

        _liveSyncTooltip ??= LiveSyncToggle.ToolTip;
        LiveSyncToggle.IsEnabled = _pulseUnlocked;
        LiveSyncToggle.Opacity = _pulseUnlocked ? 1.0 : 0.5;
        LiveSyncToggle.ToolTip = _pulseUnlocked ? _liveSyncTooltip : LockedTooltip;

        if (_devMode || _sentryUnlocked || _pulseUnlocked)
            CommandManager.InvalidateRequerySuggested();
    }

    private void ApplyLicenseStateToMenu()
    {
        SetMenuLock(MenuPulse, !_pulseUnlocked);

        if (_devMode)
        {
            ForceEnableMenu(MenuFileRoot);
            ForceEnableMenu(MenuPulse);
            ForceEnableMenu(MenuGraphRoot);
        }

        AppendAuditLine($"Menu Bar Status: {(IsMenuBarEnabled() ? "Enabled" : "Disabled")}");
    }

    private void SetMenuLock(MenuItem item, bool locked)
    {
        if (!_menuHeaders.TryGetValue(item, out var baseHeader))
        {
            baseHeader = item.Header?.ToString() ?? string.Empty;
            _menuHeaders[item] = baseHeader;
        }

        item.Opacity = locked ? 0.5 : 1.0;
        item.IsEnabled = !locked;
        item.ToolTip = locked ? LockedTooltip : null;
        item.Header = locked ? $"{baseHeader} 🔒" : baseHeader;
    }

    private void ForceEnableMenu(MenuItem item)
    {
        if (!_menuHeaders.TryGetValue(item, out var baseHeader))
        {
            baseHeader = item.Header?.ToString() ?? string.Empty;
            _menuHeaders[item] = baseHeader;
        }

        item.IsEnabled = true;
        item.Opacity = 1.0;
        item.ToolTip = null;
        item.Header = baseHeader;
    }

    private bool IsMenuBarEnabled()
        => MenuFileRoot.IsEnabled && MenuPulse.IsEnabled && MenuGraphRoot.IsEnabled;

    private void AppendAuditLine(string message)
    {
        try
        {
            var auditPath = System.IO.Path.Combine(DataDirectory, "kanon_audit.log");
            System.IO.File.AppendAllText(auditPath, $"{message}{Environment.NewLine}");
        }
        catch { }
    }

    private void ApplyLicenseStateToPalette()
    {
        if (PaletteList.Items.Count == 0) return;
        foreach (var item in PaletteList.Items)
        {
            if (item is not NodeSchema schema) continue;
            if (PaletteList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
                container.Tag = IsSchemaLocked(schema);
        }
    }

    private void ShowLockedMessage()
    {
        MessageBox.Show(
            LockedTooltip,
            "Sovereign Tier Required",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ── Sovereign Discovery onboarding ──────────────────────────────
    private bool IsOnboardingCompleted()
    {
        if (!System.IO.File.Exists(ConfigPath)) return false;
        try
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(ConfigPath));
            if (doc.RootElement.TryGetProperty("onboardingCompleted", out var value))
                return value.GetBoolean();
        }
        catch { }
        return false;
    }

    private void MarkOnboardingCompleted()
    {
        System.IO.Directory.CreateDirectory(DataDirectory);
        var payload = JsonSerializer.Serialize(new { onboardingCompleted = true }, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        System.IO.File.WriteAllText(ConfigPath, payload);
    }

    private void ShowOnboardingIfFirstRun()
    {
        if (IsOnboardingCompleted()) return;
        // Show the welcome overlay instead of the popup tour
        WelcomeOverlay.Visibility = Visibility.Visible;
    }

    private void ShowOnboardingStep(int step)
    {
        if (_onboardingPopup != null)
            _onboardingPopup.IsOpen = false;

        string sentryStatus = StatusLabel(_sentryUnlocked);
        string pulseStatus = StatusLabel(_pulseUnlocked);

        string message;
        UIElement target;
        PlacementMode placement;

        if (step == 1)
        {
            message = $"This is your Sentry Kernel. It ensures data sovereignty by signing intents locally. [Status: {sentryStatus}]";
            target = StatusSentryText;
            placement = PlacementMode.Top;
        }
        else if (step == 2)
        {
            message = $"BridgeMod connects your unique hardware ID to the suite for machine-locked security. [Status: {pulseStatus}]";
            target = MenuPulse;
            placement = PlacementMode.Bottom;
        }
        else if (step == 3)
        {
            message = "DreamCraft Studio: Build without subscriptions. You own the hardware; you own the code.";
            target = this;
            placement = PlacementMode.Center;
        }
        else
        {
            message = "Ready to build? Right-click here to drop your first Sovereign Node. Every connection is secured by your local kernel.";
            target = CanvasBorder;
            placement = PlacementMode.Center;
        }

        var text = new TextBlock
        {
            Text = message,
            Foreground = Brushes.White,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280
        };

        var button = new Button
        {
            Content = step == 4 ? "Finish" : "Next",
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3E)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77))
        };

        button.Click += (_, _) =>
        {
            if (_onboardingPopup != null)
                _onboardingPopup.SetCurrentValue(Popup.IsOpenProperty, false);
            if (step < 4)
                ShowOnboardingStep(step + 1);
            else
                MarkOnboardingCompleted();
        };

        var stack = new StackPanel();
        stack.Children.Add(text);
        stack.Children.Add(button);

        _onboardingPopup = new Popup
        {
            PlacementTarget = target,
            Placement = placement,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x66)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Child = stack
            }
        };

        _onboardingPopup.IsOpen = true;
    }

    // ── Icon Geometry Paths (Phase 7) ─────────────────────────────────
    private static Geometry GetIconGeometry(string iconName) => iconName switch
    {
        "bolt"       => Geometry.Parse("M 11,0 L 5,8 L 9,8 L 5,16 L 13,8 L 8,8 Z"),
        "shield"     => Geometry.Parse("M 8,0 L 14,3 L 14,10 C 14,14 8,16 8,16 C 8,16 2,14 2,10 L 2,3 Z"),
        "calculator" => Geometry.Parse(
            "M 1,0 L 15,0 L 15,16 L 1,16 Z " +
            "M 2,1 L 14,1 L 14,6 L 2,6 Z " +
            "M 2,8 L 6,8 L 6,11 L 2,11 Z " +
            "M 7,8 L 11,8 L 11,11 L 7,11 Z " +
            "M 12,8 L 15,8 L 15,11 L 12,11 Z " +
            "M 2,12 L 6,12 L 6,15 L 2,15 Z " +
            "M 7,12 L 15,12 L 15,15 L 7,15 Z"),
        "database"   => Geometry.Parse(
            "M 1,0 L 15,0 L 15,4 L 1,4 Z " +
            "M 1,6 L 15,6 L 15,10 L 1,10 Z " +
            "M 1,12 L 15,12 L 15,16 L 1,16 Z"),
        _            => Geometry.Parse("M 8,0 C 12,0 16,4 16,8 C 16,12 12,16 8,16 C 4,16 0,12 0,8 C 0,4 4,0 8,0 Z"),
    };

    // ── Phase 11: Staleness and Wire Hit Paths ──────────────────────────
    /// <summary>
    /// Dims all visible value bubbles to signal stale data.
    /// Guarded by _graphIsStale to avoid O(n) work on every drag frame.
    /// </summary>
    private void MarkGraphStale()
    {
        if (_graphIsStale) return;
        _graphIsStale = true;
        foreach (var bubble in _portValueMap.Values)
            if (bubble.Visibility == Visibility.Visible)
                bubble.Opacity = 0.35;
        StatusPulseText.Opacity = 0.35;  // Phase 12: dim pulse time on stale
    }

    /// <summary>
    /// Restores full opacity on all visible bubbles. Called at the start of each Pulse.
    /// </summary>
    private void ClearStaleness()
    {
        _graphIsStale = false;
        foreach (var bubble in _portValueMap.Values)
            if (bubble.Visibility == Visibility.Visible)
                bubble.Opacity = 1.0;
        StatusPulseText.Opacity = 1.0;  // Phase 12: restore pulse time opacity
    }

    /// <summary>
    /// Creates a transparent hit-area Path sharing the same geometry as the visual wire.
    /// Enables hover-to-reveal tooltips for wire values.
    /// </summary>
    private static Path MakeWireHitPath(PathGeometry geometry, Guid synapseId)
    {
        return new Path
        {
            Data             = geometry,             // shared reference — auto-updates with visual wire
            Stroke           = Brushes.Transparent,  // transparent but hittable
            StrokeThickness  = 16,                   // wide target area for comfortable hover
            IsHitTestVisible = true,
            Tag              = synapseId,
            ToolTip          = new ToolTip
            {
                Content     = "No data — press F5 to Pulse",
                Background  = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
                Foreground  = new SolidColorBrush(Color.FromRgb(0xCC, 0xFF, 0xCC)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x66)),
                Padding     = new Thickness(6, 4, 6, 4),
                FontFamily  = new FontFamily("Consolas"),
                FontSize    = 11
            }
        };
    }

    // ── Palette interaction ──────────────────────────────────────────
    private void PaletteList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void PaletteList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PaletteList.SelectedItem is NodeSchema schema)
            PlaceNodeOnCanvas(schema, 80, 80);
    }

    // ── Node placement ───────────────────────────────────────────────
    private void PlaceNodeOnCanvas(NodeSchema schema, double x, double y)
    {
        if (IsSchemaLocked(schema))
        {
            ShowLockedMessage();
            return;
        }
        if (!_vm.PlaceNode(schema, x, y)) return;  // Trial: node not added
        var nodeVm = _vm.PlacedNodes[^1];

        var card = BuildNodeCard(nodeVm);
        if (Math.Abs(x) < 0.001 && Math.Abs(y) < 0.001)
        {
            x = 150;
            y = 150;
        }
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
        Panel.SetZIndex(card, 1);  // nodes always above wires (ZIndex=0)

        // ── UI Thread Enforcement ──
        Application.Current.Dispatcher.Invoke(() =>
        {
            GraphCanvas.Children.Add(card);
            _cardMap[nodeVm.Id] = card;
        });
    }

    // ── Node card construction (now instance method for _portDotMap access) ──
    private FrameworkElement BuildNodeCard(NodeViewModel vm)
    {
        // ── Phase 7: Accent color from category ──
        var accentColor = GetCategoryAccentColor(vm.Category);
        var accentBrush = new SolidColorBrush(accentColor);

        // ── Phase 7: Icon in title bar ──
        var iconPath = new Path
        {
            Data              = GetIconGeometry(vm.IconName),
            Fill              = Brushes.White,
            Width             = 14,
            Height            = 14,
            Stretch           = Stretch.Uniform,
            Margin            = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Phase 14: Store schema for popup; right-click opens schema info popup
        iconPath.Tag    = vm.Schema;
        iconPath.Cursor = Cursors.Help;
        iconPath.MouseRightButtonDown += (s, e) =>
        {
            if (s is not Path ip || ip.Tag is not NodeSchema schema) return;
            var popup = MakeSchemaPopup(schema);
            popup.PlacementTarget = ip;
            popup.IsOpen = true;
            e.Handled = true;
        };

        var titleContent = new StackPanel { Orientation = Orientation.Horizontal };
        titleContent.Children.Add(iconPath);
        titleContent.Children.Add(new TextBlock
        {
            Text       = vm.Name,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize   = 12
        });

        var titleBar = new Border
        {
            Background   = accentBrush,
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Padding      = new Thickness(8, 4, 8, 4),
            Child        = titleContent
        };

        var portStack = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        int maxPorts  = Math.Max(vm.Inputs.Count, vm.Outputs.Count);

        for (int i = 0; i < maxPorts; i++)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (i < vm.Inputs.Count)
            {
                var port = vm.Inputs[i];
                var dot  = MakePortDot(Brushes.CornflowerBlue, vm.Id, port.Name, port.Type, isOutput: false);
                var label = new TextBlock
                {
                    Text              = port.Name,
                    Foreground        = Brushes.LightGray,
                    FontSize          = 10,
                    Margin            = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                // ── Phase 7: Pill-shaped value bubble for input port ──
                var bubbleText = new TextBlock
                {
                    FontSize   = 8,
                    Foreground = new SolidColorBrush(accentColor),
                    FontFamily = new FontFamily("Consolas")
                };
                var bubble = new Border
                {
                    CornerRadius    = new CornerRadius(4),
                    Background      = new SolidColorBrush(Color.FromRgb(0x0D, 0x12, 0x1F)),
                    BorderBrush     = new SolidColorBrush(Color.FromArgb(0x99, accentColor.R, accentColor.G, accentColor.B)),
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(4, 1, 4, 1),
                    Margin          = new Thickness(2, 0, 2, 0),
                    Visibility      = Visibility.Collapsed,
                    Child           = bubbleText
                };
                _portValueMap[(vm.Id, port.Name, false)] = bubble;

                var inputPanel = new StackPanel { Orientation = Orientation.Horizontal };
                inputPanel.Children.Add(dot);
                inputPanel.Children.Add(label);
                inputPanel.Children.Add(bubble);
                Grid.SetColumn(inputPanel, 0);
                row.Children.Add(inputPanel);
            }

            if (i < vm.Outputs.Count)
            {
                var port = vm.Outputs[i];
                var dot  = MakePortDot(Brushes.LightGreen, vm.Id, port.Name, port.Type, isOutput: true);
                var label = new TextBlock
                {
                    Text                = port.Name,
                    Foreground          = Brushes.LightGray,
                    FontSize            = 10,
                    Margin              = new Thickness(0, 0, 6, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment   = VerticalAlignment.Center
                };

                // ── Phase 7: Pill-shaped value bubble for output port ──
                var bubbleText = new TextBlock
                {
                    FontSize   = 8,
                    Foreground = new SolidColorBrush(accentColor),
                    FontFamily = new FontFamily("Consolas")
                };
                var bubble = new Border
                {
                    CornerRadius    = new CornerRadius(4),
                    Background      = new SolidColorBrush(Color.FromRgb(0x0D, 0x12, 0x1F)),
                    BorderBrush     = new SolidColorBrush(Color.FromArgb(0x99, accentColor.R, accentColor.G, accentColor.B)),
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(4, 1, 4, 1),
                    Margin          = new Thickness(2, 0, 2, 0),
                    Visibility      = Visibility.Collapsed,
                    Child           = bubbleText
                };
                _portValueMap[(vm.Id, port.Name, true)] = bubble;

                var outputPanel = new StackPanel
                {
                    Orientation         = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                outputPanel.Children.Add(bubble);
                outputPanel.Children.Add(label);
                outputPanel.Children.Add(dot);
                Grid.SetColumn(outputPanel, 1);
                row.Children.Add(outputPanel);
            }

            portStack.Children.Add(row);
        }

        // ── Phase 7: Separate border brush for animation (not frozen) ──
        var borderBrush = new SolidColorBrush(accentColor);
        _cardBorderBrushMap[vm.Id] = borderBrush;

        var card = new Border
        {
            Width           = 200,
            Height          = 120,
            Background      = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
            BorderBrush     = Brushes.White,
            BorderThickness = new Thickness(2),
            CornerRadius    = new CornerRadius(5),
            Tag             = vm.Id,  // <-- Tag used by card drag handler
            Child           = new StackPanel { Children = { titleBar, portStack } }
        };

        // Add tooltip with node description
        if (!string.IsNullOrWhiteSpace(vm.Description))
        {
            card.ToolTip = new ToolTip
            {
                Content     = vm.Description,
                Background  = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
                Foreground  = new SolidColorBrush(Color.FromRgb(0xCC, 0xFF, 0xCC)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x66)),
                Padding     = new Thickness(6, 4, 6, 4),
                FontFamily  = new FontFamily("Consolas"),
                FontSize    = 11
            };
        }

        card.MouseDown += Card_MouseDown;
        return card;
    }

    // ── Port dot factory ─────────────────────────────────────────────
    private Ellipse MakePortDot(Brush fill, Guid nodeId, string portName, string portType, bool isOutput)
    {
        // Tag tuple carries all metadata needed for wire dragging and hit-testing
        var dot = new Ellipse
        {
            Width             = 10,
            Height            = 10,
            Fill              = fill,
            Margin            = new Thickness(0, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Tag               = (nodeId, portName, portType, isOutput),
            Cursor            = Cursors.Cross
        };

        dot.MouseDown += PortDot_MouseDown;
        _portDotMap[(nodeId, portName, isOutput)] = dot;
        return dot;
    }

    // ────────────────────────────────────────────────────────────────
    //  NODE DRAGGING
    // ────────────────────────────────────────────────────────────────

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only left-button drag; not during wire-drag or pan
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_isWiring || _isPanning) return;
        if (Keyboard.IsKeyDown(Key.Space)) return;  // Space is reserved for pan

        var card = (FrameworkElement)sender;
        if (card.Tag is not Guid nodeId) return;

        _isDraggingNode  = true;
        _dragNodeId      = nodeId;
        _dragNodeStart   = e.GetPosition(GraphCanvas);
        _dragNodeOriginX = Canvas.GetLeft(card);
        _dragNodeOriginY = Canvas.GetTop(card);

        card.CaptureMouse();
        e.Handled = true;  // Prevents GraphCanvas_MouseDown from also firing
    }

    // ────────────────────────────────────────────────────────────────
    //  PORT DOT — WIRE DRAG START
    // ────────────────────────────────────────────────────────────────

    private void PortDot_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not Ellipse dot) return;
        if (dot.Tag is not (Guid nodeId, string portName, string portType, bool isOutput)) return;

        // Dragging from an input port is not allowed — only output ports initiate wires
        if (!isOutput) return;

        _isWiring             = true;
        _wireSourceNodeId     = nodeId;
        _wireSourcePortName   = portName;
        _wireSourcePortType   = portType;
        _wireSourceCanvasPos  = GetPortDotCenter(dot);

        // Create ghost wire Path and add it behind all nodes (ZIndex = 0)
        _ghostWire = MakeWirePath(_wireSourceCanvasPos, _wireSourceCanvasPos, WireNormalBrush);
        Panel.SetZIndex(_ghostWire, 0);
        GraphCanvas.Children.Add(_ghostWire);

        // Capture on the canvas so MouseMove/Up fire even over child elements
        GraphCanvas.CaptureMouse();
        e.Handled = true;  // Prevents Card_MouseDown and GraphCanvas_MouseDown from firing
    }

    // ────────────────────────────────────────────────────────────────
    //  CANVAS MOUSE EVENTS — unified dispatcher
    // ────────────────────────────────────────────────────────────────

    private void GraphCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Wire-drag and node-drag are initiated on children (PortDot / Card),
        // which set e.Handled = true. If we reach here, neither is active.
        if (_isWiring || _isDraggingNode) return;

        if (e.RightButton == MouseButtonState.Pressed)
        {
            OpenCanvasContextMenu();
            e.Handled = true;
            return;
        }

        if (e.MiddleButton == MouseButtonState.Pressed ||
            (e.LeftButton  == MouseButtonState.Pressed && Keyboard.IsKeyDown(Key.Space)))
        {
            _isPanning  = true;
            _panStart   = e.GetPosition(CanvasBorder);
            _panOriginX = CanvasTranslate.X;
            _panOriginY = CanvasTranslate.Y;
            GraphCanvas.CaptureMouse();
            e.Handled = true;
        }
    }

    private void BuildCanvasContextMenu()
    {
        var addNodeItem = new MenuItem
        {
            Header = "Add Node...",
            Foreground = Brushes.White
        };
        addNodeItem.Click += (_, _) => FocusNodeSearch();

        var newProjectItem = new MenuItem
        {
            Header = "New Project",
            Foreground = Brushes.White
        };
        newProjectItem.Click += FileNew_Click;

        _canvasContextMenu = new ContextMenu();
        _canvasContextMenu.Items.Add(addNodeItem);
        _canvasContextMenu.Items.Add(new Separator());
        _canvasContextMenu.Items.Add(newProjectItem);
    }

    private void OpenCanvasContextMenu()
    {
        if (_canvasContextMenu == null)
            BuildCanvasContextMenu();

        _canvasContextMenu!.PlacementTarget = GraphCanvas;
        _canvasContextMenu.Placement = PlacementMode.MousePoint;
        _canvasContextMenu.IsOpen = true;
    }

    private void FocusNodeSearch()
    {
        NodeSearchBox.Focus();
        NodeSearchBox.SelectAll();
    }

    private void GraphCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        // ── Pan branch ──
        if (_isPanning)
        {
            var pos   = e.GetPosition(CanvasBorder);
            var delta = pos - _panStart;
            CanvasTranslate.X = _panOriginX + delta.X;
            CanvasTranslate.Y = _panOriginY + delta.Y;
            return;
        }

        // ── Node-drag branch ──
        if (_isDraggingNode)
        {
            var pos   = e.GetPosition(GraphCanvas);
            var delta = pos - _dragNodeStart;
            double newX = _dragNodeOriginX + delta.X;
            double newY = _dragNodeOriginY + delta.Y;

            if (_cardMap.TryGetValue(_dragNodeId, out var card))
            {
                Canvas.SetLeft(card, newX);
                Canvas.SetTop(card, newY);
            }

            // Update NodeViewModel position (for future serialization)
            var nodeVm = _vm.PlacedNodes.FirstOrDefault(n => n.Id == _dragNodeId);
            if (nodeVm is not null)
            {
                nodeVm.X = newX;
                nodeVm.Y = newY;
            }

            // Refresh all wires connected to this node
            RefreshSynapsesForNode(_dragNodeId);
            return;
        }

        // ── Wire-drag branch ──
        if (_isWiring && _ghostWire is not null)
        {
            var mousePos = e.GetPosition(GraphCanvas);

            // Hit-test to find if we're hovering over an input port dot
            bool compatible = false;
            var  hitResult  = VisualTreeHelper.HitTest(GraphCanvas, mousePos);
            if (hitResult?.VisualHit is Ellipse hoveredDot &&
                hoveredDot.Tag is (Guid hn, string hp, string hType, bool hIsOutput) &&
                !hIsOutput &&                              // must be an input port
                hn != _wireSourceNodeId)                  // no self-loop
            {
                compatible = _wireSourcePortType == "any" ||
                             hType               == "any" ||
                             _wireSourcePortType == hType;
            }
            else
            {
                compatible = true;  // hovering over nothing: show normal grey
            }

            _ghostWire.Stroke = compatible ? WireNormalBrush : WireInvalidBrush;
            UpdateWirePath(_ghostWire, _wireSourceCanvasPos, mousePos);
        }
    }

    private void GraphCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // ── End pan ──
        if (_isPanning)
        {
            _isPanning = false;
            GraphCanvas.ReleaseMouseCapture();
            return;
        }

        // ── End node-drag ──
        if (_isDraggingNode)
        {
            _isDraggingNode = false;
            if (_cardMap.TryGetValue(_dragNodeId, out var card))
                card.ReleaseMouseCapture();
            return;
        }

        // ── End wire-drag ──
        if (_isWiring)
        {
            GraphCanvas.ReleaseMouseCapture();

            var mousePos  = e.GetPosition(GraphCanvas);
            var hitResult = VisualTreeHelper.HitTest(GraphCanvas, mousePos);

            if (hitResult?.VisualHit is Ellipse targetDot &&
                targetDot.Tag is (Guid targetNodeId, string targetPortName, string targetPortType, bool targetIsOutput) &&
                !targetIsOutput &&
                targetNodeId != _wireSourceNodeId)
            {
                // Delegate type check and model creation to MainViewModel
                var synapseVm = _vm.ConnectPorts(
                    _wireSourceNodeId, _wireSourcePortName, _wireSourcePortType,
                    targetNodeId,      targetPortName,      targetPortType);

                if (synapseVm is not null)
                {
                    // Compute final endpoints from actual dot positions
                    Point startPos = GetPortDotCenter(_portDotMap[(_wireSourceNodeId, _wireSourcePortName, true)]);
                    Point endPos   = GetPortDotCenter(targetDot);

                    var wire = MakeWirePath(startPos, endPos, WireNormalBrush);
                    Panel.SetZIndex(wire, 0);
                    GraphCanvas.Children.Add(wire);
                    _wireMap[synapseVm.Id] = wire;

                    // Phase 11: Create hit-area path for wire tooltip
                    var hitPath = MakeWireHitPath((PathGeometry)wire.Data, synapseVm.Id);
                    Panel.SetZIndex(hitPath, 0);
                    GraphCanvas.Children.Add(hitPath);
                    _wireHitMap[synapseVm.Id] = hitPath;

                    // Phase 11: Mark graph stale (new synapse added)
                    MarkGraphStale();
                }
            }

            // Remove ghost wire regardless of success
            if (_ghostWire is not null)
            {
                GraphCanvas.Children.Remove(_ghostWire);
                _ghostWire = null;
            }

            // Reset wire-drag state
            _isWiring           = false;
            _wireSourceNodeId   = Guid.Empty;
            _wireSourcePortName = string.Empty;
            _wireSourcePortType = string.Empty;
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  ZOOM (unchanged from Phase 1)
    // ────────────────────────────────────────────────────────────────

    private void GraphCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl+Scroll: Zoom toward cursor (Gold: + hit-area thickness compensation)
            const double zoomFactor = 1.1;
            double scale    = e.Delta > 0 ? zoomFactor : 1.0 / zoomFactor;
            double newScale = CanvasScale.ScaleX * scale;
            if (newScale < 0.1 || newScale > 5.0) { e.Handled = true; return; }

            var mouse = e.GetPosition(GraphCanvas);
            CanvasTranslate.X -= mouse.X * (scale - 1) * CanvasScale.ScaleX;
            CanvasTranslate.Y -= mouse.Y * (scale - 1) * CanvasScale.ScaleY;
            CanvasScale.ScaleX = CanvasScale.ScaleY = newScale;

            // Gold: Keep hit areas constant in screen space
            double inv = 1.0 / newScale;
            foreach (var hp in _wireHitMap.Values)
                hp.StrokeThickness = 16.0 * inv;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            // Shift+Scroll: Pan horizontally
            CanvasTranslate.X += e.Delta * 0.5;
        }
        else
        {
            // Plain Scroll: Pan vertically
            CanvasTranslate.Y += e.Delta * 0.5;
        }
        e.Handled = true;
    }

    // ────────────────────────────────────────────────────────────────
    //  WIRE GEOMETRY HELPERS
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new Bezier wire Path from start to end.
    /// Control point horizontal offset = 50% of horizontal distance,
    /// giving a natural S-curve when nodes are side-by-side.
    /// </summary>
    private static Path MakeWirePath(Point start, Point end, Brush stroke)
    {
        var segment = new BezierSegment(
            point1: new Point(start.X + Math.Abs(end.X - start.X) * 0.5, start.Y),
            point2: new Point(end.X   - Math.Abs(end.X - start.X) * 0.5, end.Y),
            point3: end,
            isStroked: true);

        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed   = false
        };
        figure.Segments.Add(segment);

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        return new Path
        {
            Data            = geometry,
            Stroke          = stroke,
            StrokeThickness = 2,
            IsHitTestVisible = false  // wires do not intercept mouse events
        };
    }

    /// <summary>
    /// Mutates an existing wire Path's BezierSegment endpoints in-place,
    /// avoiding garbage allocation during drag and during node-move refresh.
    /// </summary>
    private static void UpdateWirePath(Path path, Point start, Point end)
    {
        if (path.Data is not PathGeometry geometry) return;
        if (geometry.Figures.Count == 0) return;
        var figure = geometry.Figures[0];
        figure.StartPoint = start;
        if (figure.Segments.Count > 0 && figure.Segments[0] is BezierSegment seg)
        {
            double dx = Math.Abs(end.X - start.X) * 0.5;
            seg.Point1 = new Point(start.X + dx, start.Y);
            seg.Point2 = new Point(end.X   - dx, end.Y);
            seg.Point3 = end;
        }
    }

    /// <summary>
    /// Returns the canvas-space center of a port dot Ellipse (5, 5 local offset
    /// because dots are 10x10 px).
    /// </summary>
    private Point GetPortDotCenter(Ellipse dot)
    {
        var transform = dot.TransformToAncestor(GraphCanvas);
        return transform.Transform(new Point(5, 5));
    }

    /// <summary>
    /// After a node is moved, recomputes endpoints for every wire touching it.
    /// Iterates only _vm.Synapses — O(n) on synapse count, acceptable for
    /// the expected graph sizes in Thalamus.
    /// </summary>
    private void RefreshSynapsesForNode(Guid nodeId)
    {
        MarkGraphStale();  // Phase 11: node moved, bubble data is stale

        foreach (var syn in _vm.Synapses)
        {
            if (syn.OutputNodeId != nodeId && syn.InputNodeId != nodeId) continue;
            if (!_wireMap.TryGetValue(syn.Id, out var wire)) continue;

            if (!_portDotMap.TryGetValue((syn.OutputNodeId, syn.OutputPortName, true),  out var outDot)) continue;
            if (!_portDotMap.TryGetValue((syn.InputNodeId,  syn.InputPortName,  false), out var inDot))  continue;

            Point startPos = GetPortDotCenter(outDot);
            Point endPos   = GetPortDotCenter(inDot);
            UpdateWirePath(wire, startPos, endPos);
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  SEARCH & FILTER (Phase 4)
    // ────────────────────────────────────────────────────────────────

    private void NodeSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool hasText = NodeSearchBox.Text.Length > 0;
        SearchPlaceholder.Visibility  = hasText ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchButton.Visibility  = hasText ? Visibility.Visible   : Visibility.Collapsed;
        _vm.FilterText = NodeSearchBox.Text.Trim();
    }

    private void NodeSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            NodeSearchBox.Clear();
            e.Handled = true;
        }
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        NodeSearchBox.Clear();
        NodeSearchBox.Focus();
    }

    // ────────────────────────────────────────────────────────────────
    //  PERSISTENCE (Phase 5)
    // ────────────────────────────────────────────────────────────────

    private void FileNew_Click(object sender, RoutedEventArgs e)
    {
        GraphCanvas.Children.Clear();
        ClearCanvas();
        _currentFilePath = null;
        UpdateWindowTitle();
        Title = "New Project - Thalamus";
        NodeSearchBox.Clear();
        try { System.IO.File.Delete(RecoveryFilePath); } catch { }  // Gold: discard stale recovery
        MessageBox.Show("New Project Created.");
    }

    private void FileOpen_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Thalamus Project (*.thlm)|*.thlm|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = ".thlm"
        };

        if (dialog.ShowDialog() == true)
        {
            var schema = GraphSchema.Load(dialog.FileName);
            if (schema != null)
            {
                ClearCanvas();
                _currentFilePath = dialog.FileName;
                ReconstructFromSchema(schema);
                ForceRenderGraph();
                if (_vm.PlacedNodes.Count > 0)
                    AppendAuditLine($"UI Render: {_vm.PlacedNodes.Count} nodes detected on Canvas.");
                UpdateWindowTitle();
                NodeSearchBox.Clear();
            }
            else
            {
                MessageBox.Show("Failed to load file", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void FileSave_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilePath != null)
        {
            TrySaveToPath(_currentFilePath);
        }
        else
        {
            FileSaveAs_Click(sender, e);
        }
    }

    private void FileSaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Thalamus Vault Files (*.thalamus)|*.thalamus|All Files (*.*)|*.*",
            DefaultExt = ".thalamus"
        };

        if (dialog.ShowDialog() == true)
        {
            _currentFilePath = dialog.FileName;
            TrySaveToPath(dialog.FileName);
            UpdateWindowTitle();
        }
    }

    private void UpdateWindowTitle()
    {
        if (_currentFilePath != null)
        {
            var fileName = System.IO.Path.GetFileName(_currentFilePath);
            Title = $"DreamCraft Thalamus — {fileName}";
        }
        else
        {
            Title = "DreamCraft Thalamus";
        }
    }

    private void TrySaveToPath(string path)
    {
        try
        {
            var schema = BuildGraphSchema();
            GraphSchema.Save(schema, path);
            MessageBox.Show("Graph saved successfully", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private GraphSchema BuildGraphSchema()
    {
        var schema = new GraphSchema { Version = "1.0.0" };

        // Phase 1: Nodes
        foreach (var nodeVm in _vm.PlacedNodes)
        {
            schema.Nodes.Add(new SavedNode
            {
                Id = nodeVm.Id,
                ProviderName = nodeVm.ProviderName ?? "Unknown",
                SchemaName = nodeVm.Name,
                X = nodeVm.X,
                Y = nodeVm.Y
            });
        }

        // Phase 2: Synapses
        foreach (var synapseVm in _vm.Synapses)
        {
            schema.Synapses.Add(new SavedSynapse
            {
                Id = synapseVm.Id,
                OutputNodeId = synapseVm.OutputNodeId,
                OutputPortName = synapseVm.OutputPortName,
                InputNodeId = synapseVm.InputNodeId,
                InputPortName = synapseVm.InputPortName
            });
        }

        return schema;
    }

    private void ClearCanvas()
    {
        GraphCanvas.Children.Clear();
        _cardMap.Clear();
        _portDotMap.Clear();
        _wireMap.Clear();
        _portValueMap.Clear();        // NEW
        _cardBorderBrushMap.Clear();  // NEW
        _wireHitMap.Clear();          // Phase 11
        _vm.ClearAll();

        // Reset pan/zoom
        CanvasScale.ScaleX = CanvasScale.ScaleY = 1.0;
        CanvasTranslate.X = CanvasTranslate.Y = 0.0;
        _isPanning = false;
        _isDraggingNode = false;
        _isWiring = false;
    }

    private void RefreshGraph()
    {
        foreach (var syn in _vm.Synapses)
        {
            if (!_wireMap.TryGetValue(syn.Id, out var wire)) continue;
            if (!_portDotMap.TryGetValue((syn.OutputNodeId, syn.OutputPortName, true),  out var outDot)) continue;
            if (!_portDotMap.TryGetValue((syn.InputNodeId,  syn.InputPortName,  false), out var inDot))  continue;
            UpdateWirePath(wire, GetPortDotCenter(outDot), GetPortDotCenter(inDot));
        }
    }

    private void ForceRenderGraph()
    {
        GraphCanvas.Visibility = Visibility.Visible;
        GraphCanvas.Opacity = 1.0;
        CanvasBorder.Visibility = Visibility.Visible;
        CanvasBorder.Opacity = 1.0;

        CanvasScale.ScaleX = 1.0;
        CanvasScale.ScaleY = 1.0;
        CanvasTranslate.X = 0.0;
        CanvasTranslate.Y = 0.0;

        foreach (var card in _cardMap.Values)
        {
            card.Visibility = Visibility.Visible;
            card.Opacity = 1.0;
            Panel.SetZIndex(card, 50);

            double left = Canvas.GetLeft(card);
            double top  = Canvas.GetTop(card);
            if (double.IsNaN(left) || double.IsInfinity(left)) left = 150;
            if (double.IsNaN(top)  || double.IsInfinity(top))  top = 150;
            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, top);
        }

        GraphCanvas.UpdateLayout();
        RefreshGraph();
    }

    private void DebugButton_Click(object sender, RoutedEventArgs e)
    {
        int nodeCount = _vm?.PlacedNodes.Count ?? 0;
        string canvasVis = GraphCanvas.Visibility.ToString();
        string bridgeStatus = _pulseUnlocked ? "Active" : "Locked";
        double canvasWidth = GraphCanvas.ActualWidth;
        double canvasHeight = GraphCanvas.ActualHeight;

        System.Diagnostics.Debug.WriteLine($"[DEBUG] Nodes: {nodeCount} | Canvas: {canvasWidth}x{canvasHeight} | Visibility: {canvasVis} | Pulse: {bridgeStatus}");

        MessageBox.Show(
            $"Nodes.Count: {nodeCount}\nCanvas Visibility: {canvasVis}\nCanvas Width: {canvasWidth}\nCanvas Height: {canvasHeight}\nPulse Status: {bridgeStatus}",
            "DEBUG",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ReconstructFromSchema(GraphSchema schema)
    {
        // Phase 1: Place all nodes (including missing ones as placeholders)
        foreach (var savedNode in schema.Nodes)
        {
            var key = (savedNode.ProviderName, savedNode.SchemaName);
            if (_paletteIndex.TryGetValue(key, out var nodeSchema))
            {
                PlaceNodeOnCanvasWithId(savedNode.Id, nodeSchema, savedNode.X, savedNode.Y);
            }
            else
            {
                // Missing Nucleus: place a placeholder in orange
                PlacePlaceholderOnCanvas(savedNode.Id, savedNode.ProviderName, savedNode.SchemaName,
                    savedNode.X, savedNode.Y);
            }
        }

        // Phase 2: Reconstruct wires (deferred until layout completes)
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            foreach (var savedSynapse in schema.Synapses)
            {
                // Only connect if both nodes exist in PlacedNodes
                var outNode = _vm.PlacedNodes.FirstOrDefault(n => n.Id == savedSynapse.OutputNodeId);
                var inNode = _vm.PlacedNodes.FirstOrDefault(n => n.Id == savedSynapse.InputNodeId);
                if (outNode == null || inNode == null) continue;

                // Find the port types
                var outPort = outNode.Outputs.FirstOrDefault(p => p.Name == savedSynapse.OutputPortName);
                var inPort = inNode.Inputs.FirstOrDefault(p => p.Name == savedSynapse.InputPortName);
                if (outPort == null || inPort == null) continue;

                _vm.ConnectPortsWithId(
                    savedSynapse.Id,
                    savedSynapse.OutputNodeId, savedSynapse.OutputPortName, outPort.Type,
                    savedSynapse.InputNodeId,  savedSynapse.InputPortName,  inPort.Type);

                // Build and place the wire path
                if (_portDotMap.TryGetValue((savedSynapse.OutputNodeId, savedSynapse.OutputPortName, true), out var outDot) &&
                    _portDotMap.TryGetValue((savedSynapse.InputNodeId,  savedSynapse.InputPortName,  false), out var inDot))
                {
                    Point startPos = GetPortDotCenter(outDot);
                    Point endPos   = GetPortDotCenter(inDot);
                    var wire = MakeWirePath(startPos, endPos, WireNormalBrush);
                    Panel.SetZIndex(wire, 0);
                    GraphCanvas.Children.Add(wire);
                    _wireMap[savedSynapse.Id] = wire;

                    // Phase 11: Create hit-area path for wire tooltip
                    var hitPath = MakeWireHitPath((PathGeometry)wire.Data, savedSynapse.Id);
                    Panel.SetZIndex(hitPath, 0);
                    GraphCanvas.Children.Add(hitPath);
                    _wireHitMap[savedSynapse.Id] = hitPath;
                }
            }
        });
    }

    private void PlaceNodeOnCanvasWithId(Guid id, NodeSchema schema, double x, double y)
    {
        if (IsSchemaLocked(schema))
            return;
        _vm.PlaceNodeWithId(id, schema, x, y);
        var nodeVm = _vm.PlacedNodes[^1];

        var card = BuildNodeCard(nodeVm);
        if (Math.Abs(x) < 0.001 && Math.Abs(y) < 0.001)
        {
            x = 150;
            y = 150;
        }
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
        Panel.SetZIndex(card, 1);

        // ── UI Thread Enforcement ──
        Application.Current.Dispatcher.Invoke(() =>
        {
            GraphCanvas.Children.Add(card);
            _cardMap[id] = card;
        });
    }

    private void PlacePlaceholderOnCanvas(Guid id, string providerName, string schemaName, double x, double y)
    {
        // Orange placeholder card for missing nucleus
        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),  // Caution Orange
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            Child = new TextBlock
            {
                Text = "⚠ Missing",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 11
            }
        };

        var errorText = new TextBlock
        {
            Text = $"Error: Missing Nucleus [{providerName}].\r\nPlease install the\r\nrequired pack.",
            Foreground = Brushes.Orange,
            FontSize = 9,
            Margin = new Thickness(8),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 160
        };

        var card = new Border
        {
            Width = 180,
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x2A, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(5),
            Tag = id,
            Child = new StackPanel { Children = { titleBar, errorText } }
        };

        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
        Panel.SetZIndex(card, 1);
        GraphCanvas.Children.Add(card);

        _cardMap[id] = card;
    }

    // ────────────────────────────────────────────────────────────────
    //  EXECUTION (Phase 6)
    // ────────────────────────────────────────────────────────────────

    private void TogglePulse() => PulseGraph();

    private void ToggleLiveSync()
    {
        AppendAuditLine($"Live Sync Toggled: {(LiveSyncToggle.IsChecked == true ? "On" : "Off")}");
    }

    private void PulseGraph()
    {
        AppendAuditLine("Pulse Heartbeat Initiated");
        if (_menuPulseBaseBackground != null)
        {
            MenuPulse.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x6E, 0x1E));
            var pulseFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            pulseFeedbackTimer.Tick += (_, _) =>
            {
                MenuPulse.Background = _menuPulseBaseBackground;
                pulseFeedbackTimer.Stop();
            };
            pulseFeedbackTimer.Start();
        }
        if (!_pulseUnlocked)
        {
            ShowLockedMessage();
            return;
        }
        if (_vm.PlacedNodes.Count == 0) return;

        // Phase 11/12: Get execution order and timing info
        var (results, executionOrder, elapsed, nodeTimes) = _vm.PulseWithOrder();
        int nodeCount = executionOrder.Count;

        // Phase 11: Clear staleness — bubbles will be fresh
        ClearStaleness();

        // Phase 12: Update status bar with pulse time
        StatusPulseText.Text   = $"Last Pulse: {elapsed.TotalMilliseconds:F1} ms";
        StatusPulseText.Opacity = 1.0;

        // ── Phase 7/11: Update port value bubbles (Border with TextBlock child) ────
        foreach (var (key, packet) in results)
        {
            if (_portValueMap.TryGetValue(key, out var bubble))
            {
                ((TextBlock)bubble.Child).Text = packet.DisplayString;
                bubble.Visibility = Visibility.Visible;
            }
        }

        // Phase 12: Append node timing to first output bubble if > 1 ms
        foreach (var (nodeId, nodeElapsed) in nodeTimes)
        {
            if (nodeElapsed.TotalMilliseconds < 1.0) continue;
            long µs = nodeElapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
            var nodeVm = _vm.PlacedNodes.FirstOrDefault(n => n.Id == nodeId);
            if (nodeVm is null) continue;
            foreach (var port in nodeVm.Outputs)
            {
                if (_portValueMap.TryGetValue((nodeId, port.Name, true), out var bubble) &&
                    bubble.Visibility == Visibility.Visible)
                {
                    ((TextBlock)bubble.Child).Text += $" ⏱{µs}µs";
                    break;  // one annotation per node
                }
            }
        }

        // Phase 11: Store results for wire tooltips
        _lastPulseResults = results;

        // Phase 11: Update wire tooltips with fresh values
        foreach (var synVm in _vm.Synapses)
        {
            if (!_wireHitMap.TryGetValue(synVm.Id, out var hitPath)) continue;
            if (hitPath.ToolTip is not ToolTip tt) continue;

            if (results.TryGetValue((synVm.OutputNodeId, synVm.OutputPortName, true), out var packet))
                tt.Content = $"{synVm.OutputPortName}: {packet.DisplayString}";
            else
                tt.Content = "No data — press F5 to Pulse";
        }

        // Phase 13: Broadcast if Live Sync is enabled
        if (LiveSyncToggle.IsChecked == true)
            _vm.BroadcastResults(results);

        // ── Phase 11: Staggered glow animation (50ms per node in execution order) ──
        for (int i = 0; i < nodeCount; i++)
        {
            if (!_cardBorderBrushMap.TryGetValue(executionOrder[i], out var brush)) continue;

            var animation = new ColorAnimation
            {
                From         = brush.Color,
                To           = Color.FromRgb(0x00, 0xE5, 0xFF),
                Duration     = new Duration(TimeSpan.FromMilliseconds(300)),
                AutoReverse  = true,
                FillBehavior = FillBehavior.Stop,
                BeginTime    = TimeSpan.FromMilliseconds(i * 50)  // Phase 11: stagger
            };

            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }

        // ── Phase 11: Staggered wire flash (by output node's step index) ────
        // Map nodeId → step index
        var stepByNode = executionOrder
            .Select((id, i) => (id, i))
            .ToDictionary(x => x.id, x => x.i);

        // Group active wires by the step of their output node
        var wiresByStep = new Dictionary<int, List<Path>>();
        foreach (var synVm in _vm.Synapses)
        {
            bool hasValue = results.ContainsKey((synVm.OutputNodeId, synVm.OutputPortName, true));
            if (!hasValue) continue;
            if (!_wireMap.TryGetValue(synVm.Id, out var wire)) continue;
            int step = stepByNode.TryGetValue(synVm.OutputNodeId, out int s) ? s : 0;
            if (!wiresByStep.TryGetValue(step, out var list))
                wiresByStep[step] = list = [];
            list.Add(wire);
        }

        // Schedule one timer per step to flash wires at that step's timing
        foreach (var (step, wires) in wiresByStep)
        {
            int delay = step * 50;
            var capturedWires = wires;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, delay)) };
            t.Tick += (_, _) =>
            {
                foreach (var w in capturedWires)
                    w.Stroke = WireActiveBrush;
                t.Stop();
            };
            t.Start();
        }

        // ── Phase 11: Restore all wires after full staggered duration ────
        int totalDuration = 600 + Math.Max(0, nodeCount - 1) * 50;
        var restoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(totalDuration) };
        restoreTimer.Tick += (_, _) =>
        {
            foreach (var wire in _wireMap.Values)
                wire.Stroke = WireNormalBrush;
            restoreTimer.Stop();
        };
        restoreTimer.Start();
    }

    // ────────────────────────────────────────────────────────────────
    //  PHASE 14: SCHEMA DOCUMENTATION POPUP
    // ────────────────────────────────────────────────────────────────

    private static Popup MakeSchemaPopup(NodeSchema schema)
    {
        var stack = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };

        // Title
        stack.Children.Add(new TextBlock
        {
            Text       = schema.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0xCC)),
            FontWeight = FontWeights.Bold,
            FontSize   = 13,
            Margin     = new Thickness(0, 0, 0, 4)
        });

        // Description
        if (!string.IsNullOrWhiteSpace(schema.Description))
            stack.Children.Add(new TextBlock
            {
                Text         = schema.Description,
                Foreground   = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xBB)),
                FontSize     = 10,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth     = 240,
                Margin       = new Thickness(0, 0, 0, 8)
            });

        // Separator
        stack.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x55)),
            Margin     = new Thickness(0, 0, 0, 6)
        });

        // Inputs section
        if (schema.Inputs.Count > 0)
        {
            stack.Children.Add(MakePopupSectionHeader("INPUTS",  Color.FromRgb(0x77, 0xBB, 0xFF)));
            foreach (var p in schema.Inputs)
                stack.Children.Add(MakePortInfoLine(p.Name, p.Type, Color.FromRgb(0x64, 0x95, 0xED)));
        }

        // Outputs section
        if (schema.Outputs.Count > 0)
        {
            stack.Children.Add(MakePopupSectionHeader("OUTPUTS", Color.FromRgb(0x77, 0xFF, 0x99),
                topMargin: schema.Inputs.Count > 0 ? 6 : 0));
            foreach (var p in schema.Outputs)
                stack.Children.Add(MakePortInfoLine(p.Name, p.Type, Color.FromRgb(0x90, 0xEE, 0x90)));
        }

        return new Popup
        {
            Child = new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x66)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(5),
                Child           = stack
            },
            StaysOpen          = false,
            AllowsTransparency = true,
            Placement          = PlacementMode.Mouse
        };
    }

    private static TextBlock MakePopupSectionHeader(string text, Color color, int topMargin = 0)
        => new TextBlock
        {
            Text       = text,
            Foreground = new SolidColorBrush(color),
            FontSize   = 9, FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, topMargin, 0, 2)
        };

    private static TextBlock MakePortInfoLine(string name, string type, Color color)
        => new TextBlock
        {
            Text       = $"  ● {name}  [{type}]",
            Foreground = new SolidColorBrush(color),
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 10
        };

    // ────────────────────────────────────────────────────────────────
    //  GOLD: GHOST RECOVERY (AUTO-SAVE)
    // ────────────────────────────────────────────────────────────────

    private void AutoSaveRecovery()
    {
        if (_vm.PlacedNodes.Count == 0) return;  // nothing worth saving
        try { GraphSchema.Save(BuildGraphSchema(), RecoveryFilePath); }
        catch { /* ignore — recovery is best-effort */ }
    }

    private void CheckRecovery()
    {
        if (!System.IO.File.Exists(RecoveryFilePath)) return;
        var schema = GraphSchema.Load(RecoveryFilePath);
        if (schema == null || schema.Nodes.Count == 0) return;

        var answer = MessageBox.Show(
            "Unsaved changes detected from a previous session.\n\nRestore?",
            "Thalamus — Recovery",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Yes)
        {
            ClearCanvas();
            ReconstructFromSchema(schema);
            UpdateWindowTitle();
        }

        // Delete recovery regardless — it has been offered
        try { System.IO.File.Delete(RecoveryFilePath); } catch { }
    }

    // ────────────────────────────────────────────────────────────────
    //  GOLD: AUTO-LAYOUT (TOPOLOGICAL RANK COLUMNS)
    // ────────────────────────────────────────────────────────────────

    private void AutoLayout()
    {
        if (_vm.PlacedNodes.Count == 0) return;

        // ── Step 1: Compute topological rank via BFS ──────────────────
        var inDegree   = _vm.PlacedNodes.ToDictionary(n => n.Id, _ => 0);
        var successors = _vm.PlacedNodes.ToDictionary(n => n.Id, _ => new List<Guid>());

        foreach (var syn in _vm.Synapses)
        {
            if (inDegree.ContainsKey(syn.InputNodeId))  inDegree[syn.InputNodeId]++;
            if (successors.ContainsKey(syn.OutputNodeId)) successors[syn.OutputNodeId].Add(syn.InputNodeId);
        }

        var rank  = new Dictionary<Guid, int>();
        var queue = new Queue<Guid>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        foreach (var id in queue) rank[id] = 0;

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var succId in successors[id])
            {
                int candidate = rank[id] + 1;
                if (!rank.TryGetValue(succId, out int cur) || candidate > cur)
                    rank[succId] = candidate;
                if (--inDegree[succId] == 0)
                    queue.Enqueue(succId);
            }
        }

        // ── Step 2: Assign positions by rank column ────────────────────
        const double colSpacing = 250;
        const double rowSpacing = 150;
        const double startX     = 60;
        const double startY     = 60;

        var byRank = _vm.PlacedNodes
            .GroupBy(n => rank.GetValueOrDefault(n.Id, 0))
            .OrderBy(g => g.Key);

        foreach (var group in byRank)
        {
            double x = startX + group.Key * colSpacing;
            double y = startY;
            foreach (var node in group.OrderBy(n => n.Id))  // stable order within column
            {
                node.X = x;
                node.Y = y;
                if (_cardMap.TryGetValue(node.Id, out var card))
                {
                    Canvas.SetLeft(card, x);
                    Canvas.SetTop(card, y);
                }
                y += rowSpacing;
            }
        }

        // ── Step 3: Refresh all wire geometry ─────────────────────────
        // Use Dispatcher.BeginInvoke so cards have been re-laid-out before we compute dot positions
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            foreach (var syn in _vm.Synapses)
            {
                if (!_wireMap.TryGetValue(syn.Id, out var wire)) continue;
                if (!_portDotMap.TryGetValue((syn.OutputNodeId, syn.OutputPortName, true),  out var outDot)) continue;
                if (!_portDotMap.TryGetValue((syn.InputNodeId,  syn.InputPortName,  false), out var inDot))  continue;
                UpdateWirePath(wire, GetPortDotCenter(outDot), GetPortDotCenter(inDot));
            }
            MarkGraphStale();
        });
    }

    // ── Custom window chrome handlers ─────────────────────────────────────
    private void TitleMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void TitleMaximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void TitleClose_Click(object sender, RoutedEventArgs e) => Close();

    // ── Node Nexus handlers ───────────────────────────────────────────────
    private void ImportTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Coming soon — Node Nexus is launching with v1.1.0", "Node Nexus",
                        MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportNexusButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Coming soon — Node Nexus is launching with v1.1.0", "Node Nexus",
                        MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── Welcome overlay handlers ──────────────────────────────────────────
    private void WelcomeLoadStarter_Click(object sender, RoutedEventArgs e)
    {
        // Load the starter project
        var starterPath = System.IO.Path.Combine(DataDirectory, "Starter_Project.thlm");
        if (System.IO.File.Exists(starterPath))
        {
            var schema = GraphSchema.Load(starterPath);
            if (schema != null)
            {
                ReconstructFromSchema(schema);
            }
        }
        DismissWelcomeOverlay();
    }

    private void WelcomeNexus_Click(object sender, RoutedEventArgs e)
    {
        // Find the TabControl and switch to the Node Nexus tab
        var border = (Grid)LogicalTreeHelper.GetParent(WelcomeOverlay);
        var tabControl = LogicalTreeHelper.GetChildren(border).OfType<TabControl>().FirstOrDefault();
        if (tabControl != null && tabControl.Items.Count >= 2)
        {
            tabControl.SelectedIndex = 1; // Switch to Nexus tab
        }
        DismissWelcomeOverlay();
    }

    private void DismissWelcomeOverlay()
    {
        WelcomeOverlay.Visibility = Visibility.Collapsed;
        MarkOnboardingCompleted();
    }

    // ── Node Nexus Templates ──────────────────────────────────────────
    private void LoadNodeNexusTemplates()
    {
        NexusTemplateList.Items.Clear();

        if (!System.IO.Directory.Exists(TemplatesPath))
        {
            NexusTemplateList.Items.Add(new TextBlock
            {
                Text = "(No templates yet. Check back soon!)",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99)),
                Padding = new Thickness(8, 4, 8, 4)
            });
            return;
        }

        var templateFiles = System.IO.Directory.GetFiles(TemplatesPath, "*.thlm")
            .OrderBy(f => System.IO.Path.GetFileName(f))
            .ToList();

        if (templateFiles.Count == 0)
        {
            NexusTemplateList.Items.Add(new TextBlock
            {
                Text = "(No templates yet. Check back soon!)",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99)),
                Padding = new Thickness(8, 4, 8, 4)
            });
            return;
        }

        foreach (var templateFile in templateFiles)
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(templateFile);
            var button = new Button
            {
                Content = fileName,
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x2A)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x66)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(4, 2, 4, 2),
                Cursor = Cursors.Hand,
                Tag = templateFile,  // Store full path
                FontSize = 11
            };

            button.Click += TemplateButton_Click;
            NexusTemplateList.Items.Add(button);
        }
    }

    private void TemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string templatePath) return;

        var result = MessageBox.Show(
            $"Load '{System.IO.Path.GetFileNameWithoutExtension(templatePath)}' into current canvas?",
            "Load Template",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            var schema = GraphSchema.Load(templatePath);
            if (schema != null)
            {
                ReconstructFromSchema(schema);
            }
        }
    }
}
