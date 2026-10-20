namespace NOVR.Installer;

public static class InstallerConstants
{
    public const string AppName = "Nuclear Option VR";
    public const string GitHubOwner = "MajorNeil";
    public const string GitHubRepo = "novr";
    public const string UserAgent = "NOVR.Installer";

    // The project this fork comes from. Both install into the same BepInEx/{plugins,patchers}/NOVR folders, so this
    // installer can update over or uninstall a copy installed by the original installer.
    public const string OriginalGitHubOwner = "InfernoSuperNova";
    public const string OriginalGitHubRepo = "novr";

    public const string PluginsFolderName = "plugins";
    public const string PatchersFolderName = "patchers";
    public const string ModFolderName = "NOVR";

    public const string BepInExOwner = "BepInEx";
    public const string BepInExRepo = "BepInEx";

    public const string VersionFileName = "version.txt";

    // version.txt keeps the version on its first line, which is all the original installer reads; the source line
    // after it tells which project's build is installed.
    public const string VersionFileSourcePrefix = "source=";
    public const string ForkSource = GitHubOwner + "/" + GitHubRepo;
    public const string OriginalSource = OriginalGitHubOwner + "/" + OriginalGitHubRepo;

    // NOVR.zip from dist/ is embedded into the installer when it exists at build time (see NOVR.Installer.csproj).
    public const string BundledPackageResourceName = "NOVR.zip";
    public const string BundledPackageVersionMetadataKey = "BundledNovrVersion";
}
