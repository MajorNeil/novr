using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using NOVR.Installer.Models;
using NOVR.Installer.Services;

namespace NOVR.Installer.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private sealed record NovrPackage(string ZipPath, ReleaseVersion Version, string Origin, string TempDir);


    private readonly GameLocator _gameLocator = new();
    private readonly GitHubReleaseClient _releaseClient = new();
    private readonly FileSystemInstaller _installer = new();
    private readonly ProtonPrefixService _protonPrefixService = new();

    private GameInstallInfo? _gameInfo;
    private string _gamePath = string.Empty;
    private string _status = "Ready.";
    private string _details = string.Empty;
    private string _primaryActionText = "Install";
    private string _repairUpdateText = "Repair / Update";
    private bool _isBusy;
    private bool _isInstalledMode;
    private bool _removeBepInExOnFinish;
    private bool _showUninstallFinish;
    private Func<Task<string?>>? _browseForFolderAsync;
    private Task<NovrPackage>? _novrPackageTask;
    private NovrPackage? _novrPackage;
    private object _downloadLock = new();

    public MainWindowViewModel()
    {
        PrimaryActionCommand = new AsyncCommand(PrimaryActionAsync, () => !IsBusy);
        RepairUpdateCommand = new AsyncCommand(RepairUpdateAsync, () => !IsBusy && GameInfo?.IsValid == true);
        UninstallCommand = new AsyncCommand(UninstallAsync, () => !IsBusy && GameInfo?.IsValid == true);
        FinishUninstallCommand = new AsyncCommand(FinishUninstallAsync, () => !IsBusy && GameInfo?.IsValid == true);
        RescanCommand = new AsyncCommand(ScanAsync, () => !IsBusy);
        BrowseCommand = new AsyncCommand(BrowseAsync, () => !IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string GamePath
    {
        get => _gamePath;
        set
        {
            if (SetField(ref _gamePath, value))
            {
                _gameInfo = string.IsNullOrWhiteSpace(value) ? null : _gameLocator.Inspect(value);
                RefreshMode();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string Details
    {
        get => _details;
        private set => SetField(ref _details, value);
    }

    public string PrimaryActionText
    {
        get => _primaryActionText;
        private set => SetField(ref _primaryActionText, value);
    }

    public string RepairUpdateText
    {
        get => _repairUpdateText;
        private set => SetField(ref _repairUpdateText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsInstalledMode
    {
        get => _isInstalledMode;
        private set => SetField(ref _isInstalledMode, value);
    }

    public bool IsInstallMode => !IsInstalledMode && !ShowUninstallFinish;

    public bool ShowUninstallFinish
    {
        get => _showUninstallFinish;
        private set
        {
            if (SetField(ref _showUninstallFinish, value))
            {
                OnPropertyChanged(nameof(IsInstallMode));
            }
        }
    }

    public bool RemoveBepInExOnFinish
    {
        get => _removeBepInExOnFinish;
        set => SetField(ref _removeBepInExOnFinish, value);
    }

    public GameInstallInfo? GameInfo
    {
        get => _gameInfo;
        private set
        {
            if (SetField(ref _gameInfo, value))
            {
                GamePath = value?.GameDir ?? GamePath;
            }

            // The record compares equal when only version.txt changed (e.g. after replacing the original), so the
            // labels are refreshed either way.
            RefreshMode();
        }
    }

    public ICommand PrimaryActionCommand { get; }
    public ICommand RepairUpdateCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand FinishUninstallCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand BrowseCommand { get; }

    public void SetFolderBrowser(Func<Task<string?>> browseForFolderAsync)
    {
        _browseForFolderAsync = browseForFolderAsync;
    }

    public async Task InitializeAsync()
    {
        await ScanAsync();
        var progress = new Progress<string>(message => Status = message);
        // Progress posts to the UI thread in order, so the final state goes through it too to land after the reports.
        IProgress<Action> ui = new Progress<Action>(apply => apply());

        NovrPackage package;
        try
        {
            package = await EnsureNovrPackageAsync(progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ui.Report(() =>
            {
                Status = "No NOVR build available to install.";
                Details = BuildDetails(GameInfo) + Environment.NewLine + ex.Message;
            });
            return;
        }

        ui.Report(() =>
        {
            var info = GameInfo;
            var installed = info?.Version;

            if (info?.ModState == InstallState.FullyInstalled && !info.IsForkInstall)
            {
                Status = $"NOVR {installed} from {info.InstalledSourceDescription} is installed. " +
                         $"\"{RepairUpdateText}\" installs NOVR {package.Version} in its place.";
            }
            else if (info?.ModState == InstallState.FullyInstalled && installed >= package.Version)
            {
                Status = $"NOVR is up to date. {package.Version}";
            }
            else if (info?.ModState == InstallState.FullyInstalled)
            {
                Status = $"Update available. {installed} -> {package.Version}";
            }
            else
            {
                Status = $"Ready to install NOVR {package.Version}.";
            }

            Details = BuildDetails(GameInfo);
        });
    }

    private Task<NovrPackage> EnsureNovrPackageAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        lock (_downloadLock)
        {
            _novrPackageTask ??= ResolveNovrPackageAsync(progress, cancellationToken);
            return _novrPackageTask;
        }
    }

    // Installs the newer of the build bundled with this installer and the latest release of this fork, preferring
    // the bundled one when they match so no download is needed.
    private async Task<NovrPackage> ResolveNovrPackageAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "novr-installer-" + Guid.NewGuid().ToString("N"));

        try
        {
            GitHubReleaseClient.NovrRelease? release = null;
            Exception? releaseError = null;
            try
            {
                release = await _releaseClient.TryGetLatestNovrReleaseAsync(progress, cancellationToken);
            }
            catch (Exception ex)
            {
                releaseError = ex;
            }

            NovrPackage package;
            if (BundledNovrPackage.IsAvailable && (release is null || release.Version <= BundledNovrPackage.Version))
            {
                var zip = await BundledNovrPackage.ExtractAsync(tempDir, cancellationToken);
                package = new NovrPackage(zip, BundledNovrPackage.Version, "bundled with this installer", tempDir);
            }
            else if (release is not null)
            {
                var zip = await _releaseClient.DownloadNovrReleaseAsync(release, tempDir, progress, cancellationToken);
                package = new NovrPackage(zip, release.Version, $"release from github.com/{InstallerConstants.ForkSource}", tempDir);
            }
            else
            {
                throw new InvalidOperationException(releaseError is null
                    ? $"There is no published release at github.com/{InstallerConstants.ForkSource}/releases yet, and this installer has no bundled NOVR build."
                    : $"Could not check github.com/{InstallerConstants.ForkSource}/releases ({releaseError.Message}), and this installer has no bundled NOVR build.");
            }

            _novrPackage = package;
            progress.Report($"NOVR {package.Version} ready ({package.Origin}).");
            RefreshMode();
            return package;
        }
        catch
        {
            TryDeleteDirectory(tempDir);

            lock (_downloadLock)
            {
                _novrPackageTask = null;
            }

            throw;
        }
    }
    
    
    private Task ScanAsync()
    {
        return RunBusyAsync(() =>
        {
            ShowUninstallFinish = false;
            RemoveBepInExOnFinish = false;
            Status = "Searching for Nuclear Option...";
            var found = _gameLocator.FindGame();
            if (found is null)
            {
                GameInfo = null;
                GamePath = string.Empty;
                Status = "Nuclear Option was not found automatically.";
                Details = "Choose the game folder manually. It must contain NuclearOption_Data/Managed.";
                PrimaryActionText = "Install";
            }
            else
            {
                GameInfo = found;
                Status = found.ModState == InstallState.FullyInstalled
                    ? $"NOVR installed version: {found.Version}"
                    : "Nuclear Option found.";
                Details = BuildDetails(found);
            }

            return Task.CompletedTask;
        });
    }

    private async Task BrowseAsync()
    {
        if (_browseForFolderAsync is null)
        {
            Status = "Folder picker is unavailable. Paste the Nuclear Option path into the box.";
            return;
        }

        var path = await _browseForFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            GamePath = path;
            Status = "Selected game folder.";
            Details = BuildDetails(GameInfo);
        }
    }

    private Task PrimaryActionAsync()
    {
        return InstallOrUpdateAsync(InstallType.Install);
    }

    private Task RepairUpdateAsync()
    {
        return InstallOrUpdateAsync(InstallType.Repair);
    }

    private async Task InstallOrUpdateAsync(InstallType installType)
    {
        var info = ValidateSelectedGame();
        if (info is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var progress = new Progress<string>(message => Status = message);
            var tempDir = Path.Combine(Path.GetTempPath(), "novr-installer-" + Guid.NewGuid().ToString("N"));

            try
            {
                if (!info.HasBepInEx)
                {
                    var bepInExZip = await _releaseClient.DownloadLatestBepInEx5Async(tempDir, progress, CancellationToken.None);
                    await _installer.InstallBepInExAsync(info, bepInExZip, progress, CancellationToken.None);
                    info = _gameLocator.Inspect(info.GameDir);
                }

                var package = await EnsureNovrPackageAsync(progress, CancellationToken.None);
                var replacedSource = info.ModState == InstallState.FullyInstalled && !info.IsForkInstall
                    ? info.InstalledSourceDescription
                    : null;
                var updatedFrom = info.ModState == InstallState.FullyInstalled && info.Version < package.Version
                    ? info.Version.ToString()
                    : null;

                await _installer.InstallOrUpdateNovrAsync(
                    info,
                    package.ZipPath,
                    package.Version.ToString(),
                    progress,
                    CancellationToken.None);

                var protonMessage = await _protonPrefixService.TryConfigureWinHttpOverrideAsync(info, progress, CancellationToken.None);

                GameInfo = _gameLocator.Inspect(info.GameDir);
                // Through the same Progress as the step messages, which are posted, so this one lands last.
                ((IProgress<string>)progress).Report(
                    replacedSource is not null ? $"Replaced NOVR from {replacedSource} with NOVR {package.Version}." :
                    updatedFrom is not null ? $"Update complete. NOVR {updatedFrom} → {package.Version}." :
                    $"{installType} complete. NOVR {package.Version} installed.");
                Details = BuildDetails(GameInfo) + Environment.NewLine + protonMessage;
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        });
    }

    private async Task UninstallAsync()
    {
        var info = ValidateSelectedGame();
        if (info is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var progress = new Progress<string>(message => Status = message);
            var removedSource = info.InstalledSourceDescription;
            await _installer.UninstallNovrAsync(info, progress, CancellationToken.None);
            GameInfo = _gameLocator.Inspect(info.GameDir);
            ShowUninstallFinish = true;
            IsInstalledMode = false;
            ((IProgress<string>)progress).Report($"NOVR from {removedSource} was uninstalled.");
            Details = "Optionally remove BepInEx, then click Finish to return to the install screen.";
        });
    }

    private async Task FinishUninstallAsync()
    {
        var info = ValidateSelectedGame();
        if (info is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            if (RemoveBepInExOnFinish)
            {
                var progress = new Progress<string>(message => Status = message);
                await _installer.UninstallBepInExAsync(info, progress, CancellationToken.None);
            }

            ShowUninstallFinish = false;
            GameInfo = _gameLocator.Inspect(info.GameDir);
            Status = "Ready to install.";
            Details = BuildDetails(GameInfo);
        });
    }

    private GameInstallInfo? ValidateSelectedGame()
    {
        if (string.IsNullOrWhiteSpace(GamePath))
        {
            Status = "Select your Nuclear Option folder first.";
            Details = "The selected path must contain NuclearOption_Data/Managed.";
            return null;
        }

        var info = _gameLocator.Inspect(GamePath);
        if (!info.IsValid)
        {
            Status = "Invalid Nuclear Option folder.";
            Details = info.Message ?? "The selected path must contain NuclearOption_Data/Managed.";
            GameInfo = info;
            return null;
        }

        GameInfo = info;
        return info;
    }

    private void RefreshMode()
    {
        if (ShowUninstallFinish)
        {
            OnPropertyChanged(nameof(IsInstallMode));
            return;
        }

        IsInstalledMode = GameInfo?.ModState == InstallState.FullyInstalled;
        PrimaryActionText = GameInfo?.ModState == InstallState.PartiallyInstalled ? "Repair Install" : "Install";
        RepairUpdateText = GameInfo switch
        {
            { ModState: InstallState.FullyInstalled, IsForkInstall: false } => "Replace with this build",
            _ when _novrPackage is not null && GameInfo?.Version < _novrPackage.Version => $"Update to {_novrPackage.Version}",
            _ => "Repair / Update"
        };
        OnPropertyChanged(nameof(IsInstallMode));
        RaiseCommandStates();
    }

    private string BuildDetails(GameInstallInfo? info)
    {
        if (info is null)
        {
            return "No game folder selected.";
        }

        var novr = info.ModState == InstallState.FullyInstalled
            ? $"{info.ModState}, version {info.Version} from {info.InstalledSourceDescription}"
            : info.ModState.ToString();
        var available = _novrPackage is null
            ? string.Empty
            : $"{Environment.NewLine}Available: NOVR {_novrPackage.Version} ({_novrPackage.Origin})";

        return $"Game: {info.GameDir}{Environment.NewLine}" +
               $"BepInEx: {(info.HasBepInEx ? "installed" : "not installed")}{Environment.NewLine}" +
               $"NOVR: {novr}" +
               available;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await action();
        }
        catch (Exception ex)
        {
            Status = "Operation failed.";
            Details = ex.Message; 
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommandStates()
    {
        (PrimaryActionCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (RepairUpdateCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (UninstallCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (FinishUninstallCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (RescanCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (BrowseCommand as AsyncCommand)?.RaiseCanExecuteChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
