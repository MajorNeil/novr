using NOVR.Installer.Services;

namespace NOVR.Installer.Models;

public sealed record GameInstallInfo(
    string GameDir,
    bool IsValid,
    bool HasBepInEx,
    InstallState ModState,
    string? Message)
{
    public string ManagedDir => Path.Combine(GameDir, "NuclearOption_Data", "Managed");
    public string BepInExDir => Path.Combine(GameDir, "BepInEx");
    public string BepInExCoreDir => Path.Combine(BepInExDir, "core");
    public string PluginDir => Path.Combine(BepInExDir, "plugins", InstallerConstants.ModFolderName);
    public string PatcherDir => Path.Combine(BepInExDir, "patchers", InstallerConstants.ModFolderName);
    public string VersionFile => Path.Combine(PluginDir, InstallerConstants.VersionFileName);
    public ReleaseVersion Version => ReadVersionFile().Version;

    // Null when version.txt has no source line, i.e. it was written by the original installer or doesn't exist.
    public string? InstalledSource => ReadVersionFile().Source;

    public bool IsForkInstall => string.Equals(InstalledSource, InstallerConstants.ForkSource, StringComparison.OrdinalIgnoreCase);

    public string InstalledSourceDescription => (InstalledSource, File.Exists(VersionFile)) switch
    {
        (null, true) => $"the original NOVR installer ({InstallerConstants.OriginalSource})",
        (null, false) => "a manual install",
        _ when IsForkInstall => $"this installer ({InstallerConstants.ForkSource})",
        ({ } source, _) => source
    };

    private (ReleaseVersion Version, string? Source) ReadVersionFile()
    {
        var unknown = (ReleaseVersion)"0.0.0";
        if (!File.Exists(VersionFile))
        {
            return (unknown, null);
        }

        var text = File.ReadAllText(VersionFile);
        ReleaseVersion version;
        try
        {
            version = (ReleaseVersion)text;
        }
        catch (ArgumentException)
        {
            version = unknown;
        }

        var source = text.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(InstallerConstants.VersionFileSourcePrefix, StringComparison.OrdinalIgnoreCase))?
            [InstallerConstants.VersionFileSourcePrefix.Length..]
            .Trim();

        return (version, string.IsNullOrEmpty(source) ? null : source);
    }
}
