using System.Reflection;

namespace NOVR.Installer.Services;

// The NOVR build embedded into this installer when dist/NOVR.zip existed at build time, so it can install without
// a published GitHub release.
public static class BundledNovrPackage
{
    private static Assembly InstallerAssembly => typeof(BundledNovrPackage).Assembly;

    public static bool IsAvailable =>
        InstallerAssembly.GetManifestResourceInfo(InstallerConstants.BundledPackageResourceName) is not null;

    public static ReleaseVersion Version
    {
        get
        {
            var value = InstallerAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == InstallerConstants.BundledPackageVersionMetadataKey)?
                .Value;

            try
            {
                return (ReleaseVersion)(value ?? string.Empty);
            }
            catch (ArgumentException)
            {
                return (ReleaseVersion)"0.0.0";
            }
        }
    }

    public static async Task<string> ExtractAsync(string tempDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(tempDir);
        var destination = Path.Combine(tempDir, "NOVR-bundled.zip");

        await using var resource = InstallerAssembly.GetManifestResourceStream(InstallerConstants.BundledPackageResourceName)
                                   ?? throw new InvalidOperationException("This installer does not contain a bundled NOVR build.");
        await using var file = File.Create(destination);
        await resource.CopyToAsync(file, cancellationToken);
        return destination;
    }
}
