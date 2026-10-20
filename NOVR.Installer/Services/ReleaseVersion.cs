namespace NOVR.Installer.Services;

public readonly struct ReleaseVersion
{
    private readonly string _meta;
    public ReleaseVersion(string meta)
    {
        if (string.IsNullOrWhiteSpace(meta))
            throw new ArgumentException("Meta data cannot be empty.", nameof(meta));
        _meta = meta;
        string[] lines = _meta.Split('\n');
        if (string.IsNullOrWhiteSpace(lines[0]))
            throw new ArgumentException("Meta data couldn't find version on first line.", nameof(meta));
        
        // Release tags may be written as "v1.2.3".
        Version = lines[0].Trim().TrimStart('v', 'V');
        
        
        string[] versionParts = Version.Split('.');
        if (versionParts.Length != 3)
            throw new ArgumentException("Meta data version string is malformed.", nameof(meta));
        
        try 
        {
            Major = int.Parse(versionParts[0]);
            Minor = int.Parse(versionParts[1]);
            Patch = int.Parse(versionParts[2]);
        }
        catch (FormatException)
        {
            throw new ArgumentException("Meta data is not of type numeric major.minor.patch.", nameof(meta));
        }
    }
    
    public string Version { get; }
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    
    public static bool operator ==(ReleaseVersion lhs, ReleaseVersion rhs)
    {
        return 
            lhs.Major == rhs.Major &&
            lhs.Minor == rhs.Minor &&
            lhs.Patch == rhs.Patch;
    }
    public static bool operator !=(ReleaseVersion lhs, ReleaseVersion rhs)
    {
        return  !(lhs == rhs);
    }
    
    public static bool operator >(ReleaseVersion lhs, ReleaseVersion rhs)
    {
        return 
            lhs.Major > rhs.Major ||
            (lhs.Major == rhs.Major && lhs.Minor > rhs.Minor) ||
            (lhs.Major == rhs.Major && lhs.Minor == rhs.Minor && lhs.Patch > rhs.Patch);
    }

    public static bool operator <(ReleaseVersion lhs, ReleaseVersion rhs)
    {
        return 
            lhs.Major < rhs.Major ||
            (lhs.Major == rhs.Major && lhs.Minor < rhs.Minor) ||
            (lhs.Major == rhs.Major && lhs.Minor == rhs.Minor && lhs.Patch < rhs.Patch);        
    }
    
    public static bool operator >=(ReleaseVersion lhs, ReleaseVersion rhs)
    {
        return !(lhs < rhs);
    }
    public static bool operator <=(ReleaseVersion lhs, ReleaseVersion rhs)
    {
        return !(lhs > rhs);
    }
    
    public static explicit operator ReleaseVersion(string file)
    {
        return new ReleaseVersion(file);
    }

    public override string ToString()
    {
        return Version;
    }
}