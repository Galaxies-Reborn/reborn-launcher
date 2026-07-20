using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RebornLauncher.Core.Models;
using RebornLauncher.Core.Services;

namespace RebornLauncher.Desktop;

public partial class MainWindow : Window
{
    private const int MaximumActivityCharacters = 500_000;

    private readonly ProcessRunner _processRunner = new();
    private readonly ContainerService _containerService;
    private readonly Core3PipelineService _core3Pipeline;
    private readonly ClientLaunchService _clientLaunch;
    private readonly PrerequisiteService _prerequisiteService;
    private readonly PrerequisiteInstaller _prerequisiteInstaller;

    private UmbrellaService? _umbrella;
    private UmbrellaCatalog? _catalog;
    private ReleaseChannel? _selectedChannel;
    private IReadOnlyList<VariantReference> _variants = [];
    private InstanceSettings _settings = new();
    private CancellationTokenSource? _operationCancellation;
    private bool _suppressSelectionCascade;

    public MainWindow()
    {
        InitializeComponent();
        _containerService = new ContainerService(_processRunner);
        _core3Pipeline = new Core3PipelineService(_containerService, _processRunner);
        _clientLaunch = new ClientLaunchService(_processRunner);
        _prerequisiteService = new PrerequisiteService(_processRunner, _containerService);
        _prerequisiteInstaller = new PrerequisiteInstaller(_processRunner);

        // Loaded, not Opened: Opened fires before the selected tab's content is realized, so the
        // controls inside it do not exist yet.
        Loaded += OnLoaded;
        Closing += (_, _) => _operationCancellation?.Cancel();
    }

    private ReleaseChannel SelectedChannel =>
        _selectedChannel ?? throw new InvalidOperationException("No variant is selected.");

    private static string LauncherDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Galaxies Reborn",
        "Reborn Launcher");

    private static string CatalogUmbrellaRoot => Path.Combine(LauncherDataRoot, "umbrella");

    private static string LastInstancePointerPath => Path.Combine(LauncherDataRoot, "last-instance.txt");

    private static string UmbrellaRemote =>
        Environment.GetEnvironmentVariable("REBORN_UMBRELLA_REMOTE") is { Length: > 0 } remote
            ? remote
            : UmbrellaService.DefaultRemote;

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            VersionTextBlock.Text = $"Version {GetVersion()}";
            BackendComboBox.ItemsSource = ContainerService.Backends;
            BackendComboBox.SelectedItem = ContainerService.Backends[0];

            await LoadCatalogAsync();

            if (!await TryRestoreLastInstanceAsync())
            {
                ApplySettingsToControls(_settings);
            }

            UpdatePreparedUi();
            AppendActivity($"Launcher ready on {DescribeHost()}.");
            await DetectBackendAsync(showDialog: false);
            await RefreshPrerequisitesAsync();
            if (ClientLaunchService.RequiresCompatibilityLayer)
            {
                await DetectCompatibilityAsync(showDialog: false);
            }
        }
        catch (Exception exception)
        {
            // A windowed app has no console, so a startup failure would otherwise leave nothing to
            // diagnose from.
            AppendActivity($"Startup failed: {exception}");
            TryWriteStartupLog(exception);
            await ShowErrorAsync("Launcher startup failed", exception);
        }
    }

    private static void TryWriteStartupLog(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LauncherDataRoot);
            File.WriteAllText(
                Path.Combine(LauncherDataRoot, "startup-error.log"),
                $"{DateTimeOffset.Now:u}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Diagnostics must never mask the failure being reported.
        }
    }

    private static string DescribeHost() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    private async Task LoadCatalogAsync()
    {
        var bootstrapper = new GitBootstrapper(_processRunner);
        var installation = await bootstrapper.ResolveAsync();
        AppendActivity($"Using Git: {installation}");
        _umbrella = new UmbrellaService(bootstrapper.CreateService(installation));

        AppendActivity("Reading the release catalog.");
        await _umbrella.EnsureClonedAsync(UmbrellaRemote, CatalogUmbrellaRoot);
        try
        {
            await _umbrella.UpdateAsync(CatalogUmbrellaRoot);
        }
        catch (Exception exception)
        {
            AppendActivity($"Catalog refresh skipped: {exception.Message}");
        }

        _catalog = await _umbrella.LoadCatalogAsync(CatalogUmbrellaRoot);
        ProjectComboBox.ItemsSource = _catalog.Projects.Where(project => project.Available).ToList();
        ProjectComboBox.SelectedIndex = 0;
        AppendActivity($"Catalog loaded: {_catalog.Projects.Count} projects.");
    }

    private void ProjectComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionCascade || ProjectComboBox.SelectedItem is not ProjectReference project)
        {
            return;
        }

        FlavorPanel.IsVisible = project.HasFlavors;
        if (project.HasFlavors)
        {
            FlavorComboBox.ItemsSource = project.Flavors;
            FlavorComboBox.SelectedItem = project.Flavors.FirstOrDefault(flavor => flavor.Available)
                ?? project.Flavors.FirstOrDefault();
            return;
        }

        FlavorComboBox.ItemsSource = null;
        SetVariants(project.Variants);
    }

    private void FlavorComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionCascade || FlavorComboBox.SelectedItem is not FlavorReference flavor)
        {
            return;
        }

        SetVariants(flavor.Variants);
        if (flavor.Variants.Count == 0)
        {
            ChannelDescriptionTextBlock.Text =
                $"{flavor.DisplayName} has no published variant yet. Nothing will be downloaded for it.";
            DownloadSizeTextBlock.Text = string.Empty;
        }
    }

    private void RendererComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_suppressSelectionCascade)
        {
            ApplyRendererFilter();
        }
    }

    private void SetVariants(IReadOnlyList<VariantReference> variants)
    {
        _variants = variants;
        var renderers = variants
            .Select(variant => variant.Renderer)
            .Where(renderer => renderer != RendererKind.Unspecified)
            .Distinct()
            .ToList();

        _suppressSelectionCascade = true;
        try
        {
            RendererPanel.IsVisible = renderers.Count > 1;
            RendererComboBox.ItemsSource = renderers.Select(DescribeRenderer).ToList();
            RendererComboBox.SelectedIndex = renderers.Count > 0 ? 0 : -1;
        }
        finally
        {
            _suppressSelectionCascade = false;
        }

        ApplyRendererFilter();
    }

    private void ApplyRendererFilter()
    {
        var selected = RendererComboBox.SelectedItem as string;
        var filtered = RendererPanel.IsVisible && selected is not null
            ? _variants.Where(variant => DescribeRenderer(variant.Renderer) == selected).ToList()
            : _variants.ToList();

        VariantComboBox.ItemsSource = filtered;
        VariantComboBox.SelectedItem = filtered.FirstOrDefault(variant => variant.Available)
            ?? filtered.FirstOrDefault();

        if (filtered.Count == 0)
        {
            _selectedChannel = null;
            UpdateSelectionUi();
        }
    }

    private static string DescribeRenderer(RendererKind renderer) => renderer switch
    {
        RendererKind.Dx9 => "DirectX 9",
        RendererKind.Dx11 => "DirectX 11",
        _ => "Not renderer specific",
    };

    private async void VariantComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_suppressSelectionCascade)
        {
            await LoadSelectedVariantAsync();
        }
    }

    private async Task LoadSelectedVariantAsync()
    {
        if (VariantComboBox.SelectedItem is not VariantReference variant || _umbrella is null)
        {
            return;
        }

        if (!variant.Available)
        {
            _selectedChannel = null;
            ChannelDescriptionTextBlock.Text = string.IsNullOrEmpty(variant.Description)
                ? $"{variant.DisplayName} is not published yet."
                : variant.Description;
            UpdateSelectionUi();
            return;
        }

        try
        {
            _selectedChannel = await _umbrella.LoadVariantAsync(CatalogUmbrellaRoot, variant);
            UpdateSelectionUi();
            UpdatePreparedUi();
        }
        catch (Exception exception)
        {
            _selectedChannel = null;
            AppendActivity($"Unable to read variant '{variant.Id}': {exception.Message}");
            await ShowErrorAsync("Variant could not be read", exception);
        }
    }

    private void UpdateSelectionUi()
    {
        var channel = _selectedChannel;
        PrepareButton.IsEnabled = channel is not null;

        if (channel is null)
        {
            DownloadSizeTextBlock.Text = string.Empty;
            RetailClientPanel.IsVisible = false;
            return;
        }

        ChannelDescriptionTextBlock.Text = channel.Description;

        var required = UmbrellaService.GetApproximateBytes(channel, includeOptional: false);
        var optional = UmbrellaService.GetApproximateBytes(channel, includeOptional: true) - required;
        DownloadSizeTextBlock.Text = optional > 0
            ? $"Sources: about {FormatBytes(required)}, plus {FormatBytes(optional)} if build tools are included."
            : $"Sources: about {FormatBytes(required)}.";

        var optionalSubmodule = channel.Server.Submodules.FirstOrDefault(submodule => !submodule.Required);
        ClientToolsCheckBox.IsEnabled = optionalSubmodule is not null;
        ClientToolsCheckBox.Content = optionalSubmodule is null
            ? "Client build tools (none for this variant)"
            : $"{optionalSubmodule.Name} (optional, about {FormatBytes(optionalSubmodule.ApproximateBytes)})";

        RetailClientPanel.IsVisible = channel.Pipeline == PipelineKind.Core3;
        if (channel.Pipeline == PipelineKind.Core3)
        {
            UpdateRetailClientStatus();
        }
    }

    private async void PrepareButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!await TryReadSettingsAsync())
        {
            return;
        }

        var channel = SelectedChannel;
        var settings = _settings;
        var retailClient = RetailClientTextBox.Text?.Trim() ?? string.Empty;
        if (channel.Pipeline == PipelineKind.Core3)
        {
            var validation = Core3PipelineService.ValidateClientDirectory(retailClient);
            if (!validation.IsValid)
            {
                await MessageDialog.ShowAsync(this, "Retail client required", validation.Issues[0].Message);
                return;
            }
        }

        await RunOperationAsync("Preparing instance", async cancellationToken =>
        {
            var progress = new Progress<TransferProgress>(UpdateTransferProgress);
            var paths = new LauncherPaths(settings, channel);
            AppendActivity($"Preparing {channel.DisplayName} in {paths.InstanceRoot}");

            var umbrella = _umbrella ?? throw new InvalidOperationException("The release catalog is not loaded.");
            await umbrella.EnsureClonedAsync(UmbrellaRemote, paths.UmbrellaRoot, progress, cancellationToken);

            var includeOptional = ClientToolsCheckBox.IsChecked == true;
            AppendActivity(
                $"Fetching sources: about {FormatBytes(UmbrellaService.GetApproximateBytes(channel, includeOptional))}.");
            await umbrella.MaterializeVariantAsync(
                paths.UmbrellaRoot, channel, includeOptional, progress, cancellationToken);

            await InstanceConfigurationService.SaveAsync(settings, channel, cancellationToken);
            await SaveLastInstancePointerAsync(paths.SettingsPath, cancellationToken);

            if (settings.InitializeServer)
            {
                OperationProgressBar.IsIndeterminate = true;
                await RunPipelineAsync(settings, channel, paths, retailClient, cancellationToken);
            }

            UpdatePreparedUi();
            UpdateStatusBadge(settings.InitializeServer ? "Server started" : "Instance prepared", ready: true);
            AppendActivity("Instance preparation completed.");
        });
    }

    private Task RunPipelineAsync(
        InstanceSettings settings,
        ReleaseChannel channel,
        LauncherPaths paths,
        string retailClientDirectory,
        CancellationToken cancellationToken) => channel.Pipeline switch
    {
        PipelineKind.Core3 => _core3Pipeline.PrepareAndStartAsync(
            settings, paths, retailClientDirectory, AppendActivity, cancellationToken),
        _ => _containerService.InitializeAndStartAsync(settings, channel, AppendActivity, cancellationToken),
    };

    private async void DetectBackend_Click(object? sender, RoutedEventArgs e) =>
        await DetectBackendAsync(showDialog: true);

    private async Task DetectBackendAsync(bool showDialog)
    {
        var selected = (BackendComboBox.SelectedItem as ContainerBackendDefinition)?.Kind
            ?? ContainerBackendKind.Auto;
        BackendStatusTextBlock.Text = "Checking...";
        try
        {
            var backend = await _containerService.ResolveAsync(selected);
            BackendStatusTextBlock.Text = $"Ready: {backend.DisplayName}";
            ServerBackendTextBlock.Text = backend.DisplayName;
            AppendActivity($"Container environment ready: {backend.DisplayName}");
        }
        catch (Exception exception)
        {
            BackendStatusTextBlock.Text = exception.Message.Split(Environment.NewLine)[0];
            AppendActivity($"Container check: {exception.Message}");
            if (showDialog)
            {
                await ShowErrorAsync("Container environment is not ready", exception);
            }
        }
    }

    private async void DetectCompatibility_Click(object? sender, RoutedEventArgs e) =>
        await DetectCompatibilityAsync(showDialog: true);

    private async Task DetectCompatibilityAsync(bool showDialog)
    {
        var configured = CompatibilityToolTextBox?.Text?.Trim();
        try
        {
            var tool = await _clientLaunch.ResolveCompatibilityToolAsync(configured);
            if (tool is null)
            {
                SetText(CompatibilityStatusTextBlock,
                    "No Wine installation found on PATH. Install Wine, or enter a Proton wrapper above.");
                return;
            }

            SetText(CompatibilityStatusTextBlock, $"Ready: {tool}");
            _settings.CompatibilityTool = configured ?? string.Empty;
            AppendActivity($"Windows compatibility layer ready: {tool}");
        }
        catch (Exception exception)
        {
            SetText(CompatibilityStatusTextBlock, exception.Message);
            if (showDialog)
            {
                await ShowErrorAsync("Compatibility layer is not ready", exception);
            }
        }
    }

    /// <summary>Referral link. Using it earns the project a commission.</summary>
    private const string HostingReferralUri = "https://zap-hosting.com/GalaxiesReborn?voucher=montgojo-a-7826";

    private const string VpsGuideUri =
        "https://github.com/Galaxies-Reborn/reborn-launcher/blob/master/docs/VPS-DEBIAN-13.md";

    private void OpenHostingReferral_Click(object? sender, RoutedEventArgs e) =>
        _processRunner.Start(HostingReferralUri, [], Environment.CurrentDirectory);

    private void OpenVpsGuide_Click(object? sender, RoutedEventArgs e) =>
        _processRunner.Start(VpsGuideUri, [], Environment.CurrentDirectory);

    private IReadOnlyList<Prerequisite> _prerequisites = [];

    private async void RefreshPrerequisites_Click(object? sender, RoutedEventArgs e) =>
        await RefreshPrerequisitesAsync();

    private async Task RefreshPrerequisitesAsync()
    {
        if (PrerequisiteList is null)
        {
            return;
        }

        try
        {
            _prerequisites = await _prerequisiteService.InspectAsync(
                _settings,
                IncludeClientBuildCheckBox?.IsChecked == true);

            PrerequisiteList.ItemsSource = _prerequisites.Select(prerequisite => new PrerequisiteRow(
                prerequisite.Id,
                prerequisite.DisplayName + (prerequisite.Required ? string.Empty : " (optional)"),
                prerequisite.Purpose,
                prerequisite.Detail,
                prerequisite.State switch
                {
                    PrerequisiteState.Ready => "✓",
                    PrerequisiteState.NotReady => "!",
                    _ => "✕",
                },
                // Linux installs Docker through its package manager, so there is nothing to fetch.
                prerequisite.InstallerUri?.EndsWith("/", StringComparison.Ordinal) == true ||
                prerequisite.InstallerUri?.Contains("/docs.", StringComparison.Ordinal) == true
                    ? "How to install"
                    : "Download and install",
                prerequisite.CanInstall)).ToList();
        }
        catch (Exception exception)
        {
            AppendActivity($"Prerequisite check failed: {exception.Message}");
        }
    }

    private async void InstallPrerequisite_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id ||
            _prerequisites.FirstOrDefault(prerequisite => prerequisite.Id == id) is not { } prerequisite)
        {
            return;
        }

        // Linux has no installer to fetch; the distribution's package manager owns this.
        if (prerequisite.InstallerUri?.Contains("/docs.", StringComparison.Ordinal) == true)
        {
            _prerequisiteInstaller.OpenDocumentation(prerequisite);
            return;
        }

        await RunOperationAsync($"Downloading {prerequisite.DisplayName}", async cancellationToken =>
        {
            var installer = await _prerequisiteInstaller.DownloadAsync(
                prerequisite,
                Path.Combine(LauncherDataRoot, "downloads"),
                new Progress<TransferProgress>(UpdateTransferProgress),
                cancellationToken);

            AppendActivity($"Downloaded {Path.GetFileName(installer)}. Starting it now.");

            // Handed over rather than run silently: installing this changes the machine, and that
            // approval belongs to the person at the keyboard.
            _prerequisiteInstaller.Launch(installer);
            await MessageDialog.ShowAsync(
                this,
                prerequisite.DisplayName,
                $"{prerequisite.DisplayName} is installing. Finish its installer, then choose Re-check.");
        });

        await RefreshPrerequisitesAsync();
    }

    private async void BrowseInstallRoot_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Choose the Galaxies Reborn instance folder") is { } folder)
        {
            InstallRootTextBox.Text = folder;
        }
    }

    private async void OpenExistingInstance_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Choose a previously prepared instance folder") is not { } folder)
        {
            return;
        }

        var settingsPath = Path.Combine(folder, ".reborn", "instance.json");
        if (!File.Exists(settingsPath))
        {
            await MessageDialog.ShowAsync(
                this,
                "Instance configuration not found",
                $"The selected folder does not contain .reborn{Path.DirectorySeparatorChar}instance.json.");
            return;
        }

        if (await TryLoadInstanceAsync(settingsPath))
        {
            await SaveLastInstancePointerAsync(settingsPath, CancellationToken.None);
            AppendActivity($"Opened instance '{_settings.InstanceName}'.");
            UpdatePreparedUi();
        }
    }

    private async void BrowseClientDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Choose the folder holding your game client") is { } folder)
        {
            SetText(ClientDirectoryTextBox, folder);
            _settings.ClientDirectory = folder;
            UpdatePreparedUi();
        }
    }

    private async void BrowseRetailClient_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Choose your retail Star Wars Galaxies client folder") is { } folder)
        {
            RetailClientTextBox.Text = folder;
            UpdateRetailClientStatus();
        }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private void UpdateRetailClientStatus()
    {
        var folder = RetailClientTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(folder))
        {
            RetailClientStatusTextBlock.Text =
                "SWGEmu needs .tre files from a retail Pre-CU client. They are not distributed and must be supplied.";
            return;
        }

        var validation = Core3PipelineService.ValidateClientDirectory(folder);
        RetailClientStatusTextBlock.Text = validation.IsValid
            ? $"Found {Core3PipelineService.CountTreFiles(folder)} .tre files."
            : validation.Issues[0].Message;
    }

    private async void StartServer_Click(object? sender, RoutedEventArgs e)
    {
        if (!await TryReadPreparedSettingsAsync())
        {
            return;
        }

        var settings = _settings;
        var channel = SelectedChannel;
        await RunOperationAsync("Starting server", async cancellationToken =>
        {
            await InstanceConfigurationService.SaveAsync(settings, channel, cancellationToken);
            var paths = new LauncherPaths(settings, channel);
            var result = channel.Pipeline == PipelineKind.Core3
                ? await _core3Pipeline.StartAsync(settings, paths, AppendActivity, cancellationToken)
                : await _containerService.StartAsync(settings, channel, AppendActivity, cancellationToken);
            EnsureCommandSucceeded(result, "start the server");
            await RefreshServerStatusAsync(settings, channel, cancellationToken);
        });
    }

    private async void StopServer_Click(object? sender, RoutedEventArgs e)
    {
        if (!await TryReadPreparedSettingsAsync())
        {
            return;
        }

        var settings = _settings;
        var channel = SelectedChannel;
        await RunOperationAsync("Stopping server", async cancellationToken =>
        {
            var paths = new LauncherPaths(settings, channel);
            var result = channel.Pipeline == PipelineKind.Core3
                ? await _core3Pipeline.StopAsync(settings, paths, AppendActivity, cancellationToken)
                : await _containerService.StopAsync(settings, channel, AppendActivity, cancellationToken);
            EnsureCommandSucceeded(result, "stop the server");
            SetText(ServerOutputTextBox, result.CombinedOutput);
            UpdateStatusBadge("Server stopped", ready: false);
        });
    }

    private UpdateStatus? _updateStatus;

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (!await TryReadPreparedSettingsAsync())
        {
            return;
        }

        var settings = _settings;
        var channel = SelectedChannel;
        await RunOperationAsync("Checking for updates", async cancellationToken =>
        {
            var umbrella = _umbrella ?? throw new InvalidOperationException("The release catalog is not loaded.");
            var paths = new LauncherPaths(settings, channel);

            // Metadata only: asking costs nothing and fetches no source.
            _updateStatus = await umbrella.CheckForUpdateAsync(paths.UmbrellaRoot, channel, cancellationToken);
            SetText(UpdateStatusTextBlock, _updateStatus.Describe());
            SetEnabled(UpdateRebuildButton, _updateStatus.UpdateAvailable);
            AppendActivity(_updateStatus.Describe());
        });
    }

    private async void UpdateAndRebuild_Click(object? sender, RoutedEventArgs e)
    {
        if (!await TryReadPreparedSettingsAsync())
        {
            return;
        }

        var settings = _settings;
        var channel = SelectedChannel;
        var retailClient = RetailClientTextBox?.Text?.Trim() ?? string.Empty;

        await RunOperationAsync("Updating server", async cancellationToken =>
        {
            var umbrella = _umbrella ?? throw new InvalidOperationException("The release catalog is not loaded.");
            var paths = new LauncherPaths(settings, channel);
            var progress = new Progress<TransferProgress>(UpdateTransferProgress);

            AppendActivity("Stopping the server before replacing its sources.");
            if (channel.Pipeline == PipelineKind.Core3)
            {
                await _core3Pipeline.StopAsync(settings, paths, AppendActivity, cancellationToken);
            }
            else
            {
                await _containerService.StopAsync(settings, channel, AppendActivity, cancellationToken);
            }

            AppendActivity("Pulling the latest release.");
            await umbrella.UpdateAsync(paths.UmbrellaRoot, progress, cancellationToken);

            // The umbrella now records newer revisions, so materializing moves the sources onto
            // them. Submodules already fetched are still initialized, just stale, and are moved.
            var includeOptional = ClientToolsCheckBox?.IsChecked == true;
            await umbrella.MaterializeVariantAsync(
                paths.UmbrellaRoot, channel, includeOptional, progress, cancellationToken);

            AppendActivity("Rebuilding the server. This takes a while.");
            OperationProgressBar.IsIndeterminate = true;
            await RunPipelineAsync(settings, channel, paths, retailClient, cancellationToken);

            _updateStatus = await umbrella.CheckForUpdateAsync(paths.UmbrellaRoot, channel, cancellationToken);
            SetText(UpdateStatusTextBlock, _updateStatus.Describe());
            SetEnabled(UpdateRebuildButton, _updateStatus.UpdateAvailable);
            UpdateStatusBadge("Server started", ready: true);
            AppendActivity("Update complete.");
        });
    }

    private async void RefreshServer_Click(object? sender, RoutedEventArgs e)
    {
        if (!await TryReadPreparedSettingsAsync())
        {
            return;
        }

        var settings = _settings;
        var channel = SelectedChannel;
        await RunOperationAsync("Refreshing server status", token => RefreshServerStatusAsync(settings, channel, token));
    }

    private async void LoadLogs_Click(object? sender, RoutedEventArgs e)
    {
        if (!await TryReadPreparedSettingsAsync())
        {
            return;
        }

        var settings = _settings;
        var channel = SelectedChannel;
        await RunOperationAsync("Loading server logs", async cancellationToken =>
        {
            var paths = new LauncherPaths(settings, channel);
            var result = channel.Pipeline == PipelineKind.Core3
                ? await _core3Pipeline.LogsAsync(settings, paths, cancellationToken: cancellationToken)
                : await _containerService.LogsAsync(settings, channel, cancellationToken: cancellationToken);
            SetText(ServerOutputTextBox, result.CombinedOutput);
        });
    }

    private async Task RefreshServerStatusAsync(
        InstanceSettings settings,
        ReleaseChannel channel,
        CancellationToken cancellationToken)
    {
        var paths = new LauncherPaths(settings, channel);
        var result = channel.Pipeline == PipelineKind.Core3
            ? await _core3Pipeline.StatusAsync(settings, paths, cancellationToken)
            : await _containerService.StatusAsync(settings, channel, cancellationToken);
        SetText(ServerOutputTextBox, result.CombinedOutput);
        var running = result.Succeeded &&
                      (result.CombinedOutput.Contains("running", StringComparison.OrdinalIgnoreCase) ||
                       result.CombinedOutput.Contains("Up", StringComparison.OrdinalIgnoreCase));
        UpdateStatusBadge(running ? "Server running" : "Server not running", running);
        AppendActivity(running ? "Server stack is running." : "Server stack is not running.");
    }

    private async void ConfigureClients_Click(object? sender, RoutedEventArgs e)
    {
        if (!await TryReadPreparedSettingsAsync())
        {
            return;
        }

        var settings = _settings;
        var channel = SelectedChannel;
        await RunOperationAsync("Applying client connection", async cancellationToken =>
        {
            await ConfigureClientConnectionsAsync(settings, channel, cancellationToken);
            AppendActivity($"Clients now target {settings.PublicAddress}:{settings.LoginPort}.");
        });
    }

    private async void LaunchRegularClient_Click(object? sender, RoutedEventArgs e) =>
        await LaunchClientAsync(godClient: false);

    private async void LaunchGodClient_Click(object? sender, RoutedEventArgs e) =>
        await LaunchClientAsync(godClient: true);

    private async Task LaunchClientAsync(bool godClient)
    {
        if (_selectedChannel is not { } channel)
        {
            return;
        }

        var paths = new LauncherPaths(_settings, channel);
        var executable = godClient ? paths.GodClientExecutable(channel) : paths.RegularClientExecutable(channel);
        try
        {
            var configured = CompatibilityToolTextBox?.Text?.Trim();
            var plan = await _clientLaunch.LaunchAsync(executable, configured);
            AppendActivity($"Launched {(godClient ? "God client" : "regular client")}: {plan.Describe()}");
        }
        catch (Exception exception)
        {
            AppendActivity($"Client launch failed: {exception.Message}");
            await ShowErrorAsync("Client could not be launched", exception);
        }
    }

    private void OpenClientsFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedChannel is not { } channel)
        {
            return;
        }

        var paths = new LauncherPaths(_settings, channel);
        if (!Directory.Exists(paths.ClientRoot))
        {
            return;
        }

        _processRunner.Start(paths.ClientRoot, [], paths.ClientRoot);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

    private void ClearActivity_Click(object? sender, RoutedEventArgs e)
    {
        _activity.Clear();
        FlushActivity();
    }

    private void MainTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Avalonia raises this while the XAML is still loading, from the tab control's own EndInit.
        // At that point neither this control's field nor any field declared after it is assigned,
        // so the event's sender is the only reliable reference and later fields must be guarded.
        if (sender is not TabControl { SelectedItem: TabItem tab } || HeaderSubtitleTextBlock is null)
        {
            return;
        }

        HeaderSubtitleTextBlock.Text = tab.Header switch
        {
            "Server" => "Server control",
            "Clients" => "Client launch",
            "Activity" => "Installer activity",
            _ => "Instance setup",
        };

        // The tab's controls exist only from now on, so fill them once they do.
        Dispatcher.UIThread.Post(() =>
        {
            FlushActivity();
            UpdatePreparedUi();
            if (CompatibilityPanel is not null)
            {
                CompatibilityPanel.IsVisible = ClientLaunchService.RequiresCompatibilityLayer;
            }
        });
    }

    private async Task RunOperationAsync(string operation, Func<CancellationToken, Task> action)
    {
        if (_operationCancellation is not null)
        {
            return;
        }

        _operationCancellation = new CancellationTokenSource();
        SetBusy(true, operation);
        try
        {
            await action(_operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            AppendActivity($"{operation} cancelled.");
        }
        catch (Exception exception)
        {
            AppendActivity($"{operation} failed: {exception}");
            await ShowErrorAsync(operation, exception);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false, "Ready");
            UpdatePreparedUi();
        }
    }

    private void SetBusy(bool busy, string operation)
    {
        MainTabControl.IsEnabled = !busy;
        PrepareButton.IsEnabled = !busy && _selectedChannel is not null;
        CancelButton.IsEnabled = busy;
        OperationTextBlock.Text = operation;
        OperationProgressBar.IsIndeterminate = busy;
        if (!busy)
        {
            OperationProgressBar.Value = 0;
            OperationProgressBar.IsIndeterminate = false;
            TransferTextBlock.Text = string.Empty;
        }
    }

    private void UpdateTransferProgress(TransferProgress progress)
    {
        OperationProgressBar.IsIndeterminate = progress.TotalBytes <= 0;
        if (progress.TotalBytes > 0)
        {
            OperationProgressBar.Value = Math.Clamp(progress.Percentage, 0, 100);
        }

        OperationTextBlock.Text = progress.Operation;
        var speed = progress.BytesPerSecond > 0 ? $" at {FormatBytes((long)progress.BytesPerSecond)}/s" : string.Empty;
        TransferTextBlock.Text = progress.TotalBytes > 0
            ? $"{progress.CurrentItem} — {FormatBytes(progress.BytesCompleted)} of {FormatBytes(progress.TotalBytes)}{speed}"
            : progress.CurrentItem;
    }

    private async Task<bool> TryReadSettingsAsync()
    {
        if (!int.TryParse(LoginPortTextBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var loginPort))
        {
            await MessageDialog.ShowAsync(this, "Invalid login port", "Enter a numeric login port.");
            return false;
        }

        var settings = new InstanceSettings
        {
            ChannelId = SelectedChannel.Id,
            InstallRoot = InstallRootTextBox.Text?.Trim() ?? string.Empty,
            InstanceName = InstanceNameTextBox.Text?.Trim() ?? string.Empty,
            ClusterName = ClusterNameTextBox.Text?.Trim() ?? string.Empty,
            PublicAddress = PublicAddressTextBox.Text?.Trim() ?? string.Empty,
            LoginPort = loginPort,
            ContainerBackend = (BackendComboBox.SelectedItem as ContainerBackendDefinition)?.Kind
                ?? ContainerBackendKind.Auto,
            ClientDirectory = ClientDirectoryTextBox?.Text?.Trim() ?? _settings.ClientDirectory,
            InitializeServer = InitializeServerCheckBox.IsChecked == true,
            CompatibilityTool = CompatibilityToolTextBox?.Text?.Trim() ?? _settings.CompatibilityTool,
            RetailClientDirectory = RetailClientTextBox.Text?.Trim() ?? string.Empty,
            DatabasePassword = _settings.DatabasePassword,
            DatabaseAdminPassword = _settings.DatabaseAdminPassword,
        };

        var validation = InstanceConfigurationService.Validate(settings);
        if (!validation.IsValid)
        {
            await MessageDialog.ShowAsync(
                this,
                "Check instance settings",
                string.Join(Environment.NewLine, validation.Issues.Select(issue => issue.Message)));
            return false;
        }

        _settings = settings;
        return true;
    }

    private async Task<bool> TryReadPreparedSettingsAsync()
    {
        if (_selectedChannel is null || !await TryReadSettingsAsync())
        {
            return false;
        }

        var paths = new LauncherPaths(_settings, SelectedChannel);
        var composePath = SelectedChannel.Pipeline == PipelineKind.Core3 ? paths.Core3ComposePath : paths.ComposePath;
        if (!File.Exists(composePath))
        {
            await MessageDialog.ShowAsync(
                this,
                "Instance not prepared",
                "Prepare the instance before using server or client controls.");
            return false;
        }

        return true;
    }

    private void ApplySettingsToControls(InstanceSettings settings)
    {
        InstallRootTextBox.Text = settings.InstallRoot;
        InstanceNameTextBox.Text = settings.InstanceName;
        ClusterNameTextBox.Text = settings.ClusterName;
        PublicAddressTextBox.Text = settings.PublicAddress;
        LoginPortTextBox.Text = settings.LoginPort.ToString(CultureInfo.InvariantCulture);
        SetText(ClientDirectoryTextBox, settings.ClientDirectory);
        InitializeServerCheckBox.IsChecked = settings.InitializeServer;
        SetText(CompatibilityToolTextBox, settings.CompatibilityTool);
        RetailClientTextBox.Text = settings.RetailClientDirectory;
        BackendComboBox.SelectedItem = ContainerService.Backends.FirstOrDefault(
            backend => backend.Kind == settings.ContainerBackend) ?? ContainerService.Backends[0];
    }

    private async Task SelectVariantAsync(string variantId)
    {
        if (_catalog is null)
        {
            return;
        }

        foreach (var project in _catalog.Projects)
        {
            var flavor = project.Flavors.FirstOrDefault(
                candidate => candidate.Variants.Any(variant => variant.Id == variantId));
            var variants = flavor?.Variants ?? (IReadOnlyList<VariantReference>)project.Variants;
            var target = variants.FirstOrDefault(variant => variant.Id == variantId);
            if (target is null)
            {
                continue;
            }

            _suppressSelectionCascade = true;
            try
            {
                ProjectComboBox.SelectedItem = project;
                FlavorPanel.IsVisible = project.HasFlavors;
                FlavorComboBox.ItemsSource = project.HasFlavors ? project.Flavors : null;
                FlavorComboBox.SelectedItem = flavor;

                _variants = variants;
                var renderers = variants
                    .Select(candidate => candidate.Renderer)
                    .Where(renderer => renderer != RendererKind.Unspecified)
                    .Distinct()
                    .ToList();
                RendererPanel.IsVisible = renderers.Count > 1;
                RendererComboBox.ItemsSource = renderers.Select(DescribeRenderer).ToList();
                RendererComboBox.SelectedItem = DescribeRenderer(target.Renderer);

                VariantComboBox.ItemsSource = renderers.Count > 1
                    ? variants.Where(candidate => candidate.Renderer == target.Renderer).ToList()
                    : variants.ToList();
                VariantComboBox.SelectedItem = target;
            }
            finally
            {
                _suppressSelectionCascade = false;
            }

            await LoadSelectedVariantAsync();
            return;
        }
    }

    private void UpdatePreparedUi()
    {
        if (_selectedChannel is not { } channel)
        {
            return;
        }

        var paths = new LauncherPaths(_settings, channel);
        var prepared = File.Exists(paths.SettingsPath) && File.Exists(channel.Pipeline == PipelineKind.Core3
            ? paths.Core3ComposePath
            : paths.ComposePath);

        SetText(ServerSummaryTextBlock, prepared ? paths.MainRepositoryRoot : "No instance prepared");
        SetText(ServerAddressTextBlock, $"{_settings.PublicAddress}:{_settings.LoginPort}");
        SetText(ServerBackendTextBlock, ContainerService.Backends.FirstOrDefault(
            backend => backend.Kind == _settings.ContainerBackend)?.DisplayName ?? "Automatic detection");

        // Clients are the operator's own, so the controls describe what was found in their folder.
        var supported = channel.Clients.Supported;
        var chosen = !string.IsNullOrEmpty(paths.ClientRoot);
        var regular = paths.RegularClientExecutable(channel);
        var god = paths.GodClientExecutable(channel);

        SetText(RegularClientStatusTextBlock, !supported
            ? "This variant has no client. It is a server source set."
            : !chosen ? "Choose the folder holding your client."
            : File.Exists(regular) ? regular
            : $"No regular client found at {regular}");
        SetText(GodClientStatusTextBlock, !supported || !chosen
            ? string.Empty
            : File.Exists(god) ? god : $"No God client found at {god}");

        SetEnabled(LaunchRegularButton, supported && File.Exists(regular));
        SetEnabled(LaunchGodButton, supported && File.Exists(god));
        SetEnabled(ApplyLoginButton, supported && chosen);
        SetEnabled(OpenClientFolderButton, chosen);
        SetEnabled(ClientDirectoryTextBox, supported);

        if (prepared && StatusTextBlock.Text == "Setup required")
        {
            UpdateStatusBadge("Instance prepared", ready: true);
        }
    }

    private void UpdateStatusBadge(string text, bool ready)
    {
        StatusTextBlock.Text = text;
        StatusDot.Fill = new SolidColorBrush(ready ? Color.Parse("#4BC88A") : Color.Parse("#C8A24B"));
    }

    private static async Task ConfigureClientConnectionsAsync(
        InstanceSettings settings,
        ReleaseChannel channel,
        CancellationToken cancellationToken)
    {
        var paths = new LauncherPaths(settings, channel);
        var regular = paths.RegularLoginConfiguration(channel);
        var god = paths.GodLoginConfiguration(channel);
        if (File.Exists(regular))
        {
            await ClientConfigurationService.UpdateLoginAsync(
                regular, settings.PublicAddress, settings.LoginPort, cancellationToken);
        }

        if (File.Exists(god))
        {
            await ClientConfigurationService.UpdateGodLoginAsync(
                god, settings.PublicAddress, settings.LoginPort, cancellationToken);
        }
    }

    private async Task<bool> TryRestoreLastInstanceAsync()
    {
        if (!File.Exists(LastInstancePointerPath))
        {
            return false;
        }

        var settingsPath = (await File.ReadAllTextAsync(LastInstancePointerPath)).Trim();
        if (!await TryLoadInstanceAsync(settingsPath))
        {
            return false;
        }

        AppendActivity($"Restored instance '{_settings.InstanceName}'.");
        return true;
    }

    private async Task<bool> TryLoadInstanceAsync(string settingsPath)
    {
        var restored = await InstanceConfigurationService.LoadAsync(settingsPath);
        if (restored is null ||
            _catalog is null ||
            _catalog.AllVariants.All(variant => variant.Id != restored.ChannelId))
        {
            return false;
        }

        _settings = restored;
        ApplySettingsToControls(restored);
        await SelectVariantAsync(restored.ChannelId);
        return _selectedChannel is not null;
    }

    private static async Task SaveLastInstancePointerAsync(string settingsPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LastInstancePointerPath)!);
        await File.WriteAllTextAsync(LastInstancePointerPath, settingsPath, cancellationToken);
    }

    /// <summary>
    /// Source of truth for the activity log. Avalonia realizes a tab's content only once that tab
    /// is shown, so the text box does not exist until then and messages logged before that would
    /// otherwise be lost.
    /// </summary>
    private readonly System.Text.StringBuilder _activity = new();

    private void AppendActivity(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendActivity(message));
            return;
        }

        _activity.Append(CultureInfo.InvariantCulture, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        if (_activity.Length > MaximumActivityCharacters)
        {
            _activity.Remove(0, _activity.Length - MaximumActivityCharacters);
        }

        FlushActivity();
    }

    private void FlushActivity()
    {
        if (ActivityTextBox is null)
        {
            return;
        }

        ActivityTextBox.Text = _activity.ToString();
        ActivityTextBox.CaretIndex = ActivityTextBox.Text.Length;
    }

    /// <summary>Assigns text only once the owning tab has been realized.</summary>
    private static void SetText(TextBlock? target, string value)
    {
        if (target is not null)
        {
            target.Text = value;
        }
    }

    private static void SetText(TextBox? target, string value)
    {
        if (target is not null)
        {
            target.Text = value;
        }
    }

    private static void SetEnabled(Control? target, bool enabled)
    {
        if (target is not null)
        {
            target.IsEnabled = enabled;
        }
    }

    private static void EnsureCommandSucceeded(CommandResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Unable to {operation}; the container command returned {result.ExitCode}." +
                $"{Environment.NewLine}{result.CombinedOutput}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static string GetVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(informational) ? "0.4.0" : informational.Split('+')[0];
    }

    private Task ShowErrorAsync(string title, Exception exception) =>
        MessageDialog.ShowAsync(this, title, exception.Message);
}
