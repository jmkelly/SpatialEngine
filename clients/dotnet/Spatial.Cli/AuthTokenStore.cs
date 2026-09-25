namespace Spatial.Cli;

/// <summary>Private token cache used by the CLI (ADR-0071).</summary>
internal static class AuthTokenStore
{
    public static string Path =>
        Environment.GetEnvironmentVariable("SPATIAL_TOKEN_FILE")
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".spatial", "token");

    public static string? Read()
    {
        try
        {
            return File.Exists(Path) ? File.ReadAllText(Path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(string token)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new IOException("The token cache has no parent directory.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path, token);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch (IOException)
        {
            // A missing/unwritable cache is not a successful logout.
        }
    }
}
