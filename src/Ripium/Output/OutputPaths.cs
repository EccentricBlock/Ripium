using System.Text;

namespace Ripium.Output;

public static class OutputPaths
{
    /// <summary>True when <paramref name="output"/> names a file (not stdout, not the default, not a directory).</summary>
    public static bool NamesAFile(string? output) =>
        !string.IsNullOrEmpty(output) && output != "-" && !IsDirectory(output);

    /// <summary>
    /// The Markdown file to write. Without <paramref name="output"/> that is <c>./&lt;defaultName&gt;.md</c>; a directory
    /// gets <c>&lt;defaultName&gt;.md</c> inside it; a file path without an extension gets ".md" added. The parent
    /// directory is created. An existing file is an error unless <paramref name="force"/> is set.
    /// </summary>
    /// <exception cref="IOException">The file exists and <paramref name="force"/> is false.</exception>
    public static string Resolve(string? output, string defaultName, bool force)
    {
        string path;
        if (string.IsNullOrEmpty(output))
            path = defaultName + ".md";
        else if (IsDirectory(output))
            path = Path.Combine(output, defaultName + ".md");
        else
            path = WithExtension(output);

        path = Path.GetFullPath(path);
        EnsureWritable(path, force);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>The path an explicit file argument resolves to, without creating anything.</summary>
    public static string ExplicitFilePath(string output) => Path.GetFullPath(WithExtension(output));

    // "story" and "story." both mean story.md; a trailing dot would otherwise give "story..md"
    private static string WithExtension(string path)
    {
        var trimmed = path.TrimEnd('.');
        return Path.HasExtension(trimmed) ? trimmed : trimmed + ".md";
    }

    /// <summary>
    /// <c>&lt;stem&gt;_assets</c>, with the stem shortened if needed: the markdown file name may be valid at 255 bytes while the
    /// folder, seven bytes longer, would not be.
    /// </summary>
    public static string AssetsFolderName(string markdownPath)
    {
        const string suffix = "_assets";
        return FileNames.TruncateUtf8(Path.GetFileNameWithoutExtension(markdownPath), 240 - suffix.Length) + suffix;
    }

    public static void EnsureWritable(string path, bool force)
    {
        if (!force && File.Exists(path))
            throw new IOException($"{path} already exists; use --force to overwrite it");
        if (Directory.Exists(path))
            throw new IOException($"{path} is a directory");
    }

    private static bool IsDirectory(string path) =>
        Directory.Exists(path) || path.EndsWith('/') || path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Writes the file without ever replacing one that is not meant to be replaced. The text goes to a temporary file
    /// next to the target and is then moved into place; the move fails if the target exists (unless
    /// <paramref name="force"/>), so a file created after <see cref="Resolve"/> checked is still protected, and a failed
    /// write never leaves a half-written target.
    /// </summary>
    /// <exception cref="IOException">The target exists and <paramref name="force"/> is false, or the write failed.</exception>
    public static async Task WriteAsync(string path, string content, bool force, CancellationToken ct = default)
    {
        // A short, fixed-length name beside the target. Appending to the target's own name overflowed the 255-byte limit
        // for long (but valid) output names, and failed only after the whole article had been fetched.
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".ripium-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), ct);
            try
            {
                File.Move(temporary, path, overwrite: force);
            }
            catch (IOException) when (!force && File.Exists(path))
            {
                throw new IOException($"{path} already exists; use --force to overwrite it");
            }
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
