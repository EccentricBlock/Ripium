using System.Text;
using System.Text.Json;
using Ripium.Cli;
using Ripium.Markdown;
using Ripium.Medium;
using Ripium.Output;

namespace Ripium;

public static class Program
{
    private const int ExitOk = 0, ExitFailed = 1, ExitUsage = 2, ExitInternalError = 70, ExitInterrupted = 130;

    public static async Task<int> Main(string[] argv)
    {
        var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };

        CommandLineOptions options;
        try
        {
            options = CommandLine.Parse(argv);
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine($"error: {Messages.OneLine(ex.Message)}\n\n{CommandLine.Usage}");
            return ExitUsage;
        }

        if (options.Help)
        {
            Console.Out.WriteLine(CommandLine.Usage);
            return ExitOk;
        }
        if (options.Url is null)
        {
            stderr.WriteLine(CommandLine.Usage);
            return ExitUsage;
        }

        // The environment variable is checked like the flag: its value goes into an HTTP header
        var cookies = options.Cookies ?? Environment.GetEnvironmentVariable("MEDIUM_AUTH_COOKIES");
        if (!string.IsNullOrEmpty(cookies) && !CommandLine.IsValidCookieHeader(cookies))
        {
            stderr.WriteLine("error: the cookies (--cookies or MEDIUM_AUTH_COOKIES) must be printable ASCII without control characters or line breaks");
            return ExitUsage;
        }
        options = options with { Cookies = cookies };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            await RunAsync(options, stderr, cts.Token);
            return ExitOk;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            stderr.WriteLine("Cancelled.");
            return ExitInterrupted;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            stderr.WriteLine($"error: {Messages.OneLine(Describe(ex))}");
            return ExitFailed;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: unexpected {ex.GetType().Name}: {Messages.OneLine(ex.Message)}\nThis is a bug in ripium.");
            return ExitInternalError;
        }
    }

    // Everything the outside world can plausibly do to us. A timeout is a TaskCanceledException that the user did not
    // ask for, which is why it is only expected once the user-cancel case above has been ruled out.
    private static bool IsExpectedFailure(Exception ex) =>
        ex is InvalidMediumUrlException or MediumException or HttpRequestException or TaskCanceledException
            or JsonException or IOException or UnauthorizedAccessException;

    private static string Describe(Exception ex) => ex is TaskCanceledException ? "the request timed out" : ex.Message;

    private static async Task RunAsync(CommandLineOptions options, TextWriter stderr, CancellationToken ct)
    {
        var toStdout = options.Output == "-";

        // An explicit file that already exists is refused before any network traffic.
        if (OutputPaths.NamesAFile(options.Output))
            OutputPaths.EnsureWritable(OutputPaths.ExplicitFilePath(options.Output!), options.Force);

        var postId = await MediumUrl.ResolvePostIdAsync(options.Url!, MediumUrl.CreateShortLinkResolver(), ct);
        stderr.WriteLine($"Fetching post {postId}...");

        using var client = new MediumClient(options.Cookies);
        var post = Post.FromJson(await client.GetPostAsync(postId, ct));

        var rendered = MarkdownRenderer.Render(post, new RenderOptions(options.ImageWidth, options.Frontmatter));
        foreach (var warning in rendered.Warnings) stderr.WriteLine($"warning: {Messages.OneLine(warning)}");

        if (toStdout)
        {
            await using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            await stdout.WriteAsync(rendered.Markdown);
            return;
        }

        var markdownPath = OutputPaths.Resolve(options.Output, FileNames.Slugify(rendered.Title, postId), options.Force);
        var markdown = rendered.Markdown;

        if (!options.RemoteImages && rendered.ImageUrls.Count > 0)
        {
            var assetsName = OutputPaths.AssetsFolderName(markdownPath);
            var assetsDirectory = Path.Combine(Path.GetDirectoryName(markdownPath)!, assetsName);
            stderr.WriteLine($"Downloading {rendered.ImageUrls.Count} image(s) to {Messages.OneLine(assetsDirectory)}...");

            using var http = ImageDownloader.CreateHttpClient();
            var downloaded = await new ImageDownloader(http, overwrite: options.Force).DownloadAsync(
                rendered.ImageUrls, assetsDirectory, w => stderr.WriteLine($"warning: {Messages.OneLine(w)}"), ct);
            markdown = ImageDownloader.RewriteLinks(markdown, downloaded, assetsName);
        }

        await OutputPaths.WriteAsync(markdownPath, markdown, options.Force, ct);
        stderr.WriteLine($"Wrote {Messages.OneLine(markdownPath)}");
    }
}
