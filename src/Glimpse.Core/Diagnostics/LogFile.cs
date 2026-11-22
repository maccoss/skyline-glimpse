namespace Glimpse.Core.Diagnostics;

/// <summary>Keeps the tool's log from growing without end: one current file and one previous one.</summary>
public static class LogFile
{
    /// <summary>A log past this size is set aside when Glimpse starts.</summary>
    public const long MaxBytes = 5_000_000;

    /// <summary>
    /// Set the log aside once it is larger than <paramref name="maxBytes"/>: it becomes
    /// <see cref="OldPath"/>, replacing the one set aside before, and the next line starts a new log.
    /// Returns true when it did. Never throws: a log that cannot be moved is simply kept.
    /// </summary>
    public static bool RollOver(string path, long maxBytes = MaxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= maxBytes)
                return false;
            File.Move(path, OldPath(path), overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Where the previous log is kept: glimpse.log becomes glimpse.old.log.</summary>
    public static string OldPath(string path) => Path.ChangeExtension(path, ".old" + Path.GetExtension(path));
}
