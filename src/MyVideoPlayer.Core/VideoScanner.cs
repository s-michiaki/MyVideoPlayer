namespace MyVideoPlayer.Core;

public sealed record VideoFile(string FullPath, string Name, string RelativeDirectory, long SizeBytes, DateTime LastWriteTime);

/// <summary>フォルダ内の動画ファイルを列挙する</summary>
public static class VideoScanner
{
    public static readonly IReadOnlySet<string> DefaultExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".flv",
        ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".3gp", ".ogv", ".vob", ".divx",
        // 音声
        ".m4a", ".mp3", ".wav",
    };

    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".m4a", ".mp3", ".wav",
    };

    public static bool IsAudioExtension(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    public static IReadOnlyList<VideoFile> Scan(string root, bool recursive, IReadOnlySet<string>? extensions = null,
        CancellationToken ct = default)
    {
        extensions ??= DefaultExtensions;
        if (!Directory.Exists(root)) return Array.Empty<VideoFile>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };

        var result = new List<VideoFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!extensions.Contains(Path.GetExtension(path))) continue;
            try
            {
                var fi = new FileInfo(path);
                var relDir = Path.GetRelativePath(root, fi.DirectoryName ?? root);
                result.Add(new VideoFile(fi.FullName, fi.Name, relDir == "." ? "" : relDir, fi.Length, fi.LastWriteTime));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }
}
