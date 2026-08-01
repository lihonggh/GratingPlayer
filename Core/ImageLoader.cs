namespace GratingPlayer.Core;

public static class ImageLoader
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    public static bool IsSupportedImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        return SupportedExtensions.Contains(Path.GetExtension(path));
    }

    public static List<string> ScanFolder(
        string folder,
        bool includeSubdirectories = false,
        PlayOrder playOrder = PlayOrder.AsRead)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return [];

        var option = includeSubdirectories
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        try
        {
            IEnumerable<string> files = Directory.EnumerateFiles(folder, "*.*", option)
                .Where(IsSupportedImage);

            return ApplyOrder(files, playOrder).ToList();
        }
        catch
        {
            return [];
        }
    }

    public static IEnumerable<string> ApplyOrder(IEnumerable<string> files, PlayOrder playOrder)
    {
        return playOrder switch
        {
            PlayOrder.NameAsc => files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase),
            PlayOrder.NameDesc => files.OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase),
            PlayOrder.CreatedAsc => files.OrderBy(SafeGetCreationTimeUtc),
            PlayOrder.CreatedDesc => files.OrderByDescending(SafeGetCreationTimeUtc),
            PlayOrder.ModifiedAsc => files.OrderBy(SafeGetLastWriteTimeUtc),
            PlayOrder.ModifiedDesc => files.OrderByDescending(SafeGetLastWriteTimeUtc),
            _ => files // AsRead：保持枚举顺序
        };
    }

    private static DateTime SafeGetCreationTimeUtc(string path)
    {
        try
        {
            return File.GetCreationTimeUtc(path);
        }
        catch
        {
            return DateTime.MaxValue;
        }
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MaxValue;
        }
    }
}
