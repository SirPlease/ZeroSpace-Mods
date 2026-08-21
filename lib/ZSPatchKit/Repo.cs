// Finding the repo root from wherever `dotnet run` happened to start.

namespace ZSPatchKit;

public static class Repo
{
    /// Climb from the current directory until <paramref name="marker"/> resolves, as a file
    /// or a directory. Falls back to the current directory, so a miss surfaces as
    /// "input not found" rather than a wrong root.
    public static string Root(string marker)
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            var candidate = Path.Combine(dir, marker);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return Directory.GetCurrentDirectory();
    }
}
