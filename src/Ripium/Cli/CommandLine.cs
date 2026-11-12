namespace Ripium.Cli;

public sealed record CommandLineOptions(
    string? Url,
    string? Output,
    string? Cookies,
    int ImageWidth,
    bool RemoteImages,
    bool Frontmatter,
    bool Force,
    bool Help);

/// <summary>Hand-written argument parsing; the surface is small enough that a dependency would cost more than it saves.</summary>
public static class CommandLine
{
    public const int DefaultImageWidth = 1400;

    public const string Usage = """
        ripium - export a Medium article to a Markdown file

        Usage:
          ripium <url-or-post-id> [options]

        Options:
          -o, --output <path>     Output .md file, or a directory (existing, or ending in /).
                                  "-" writes to stdout. A path without an extension gets ".md" added.
                                  Default: ./<title-slug>.md
          -f, --force             Overwrite the output file if it already exists (refused by default).
              --cookies <string>  Medium cookies ("uid=...; sid=...") for member-only stories.
                                  Default: the MEDIUM_AUTH_COOKIES environment variable. Prefer the variable:
                                  command-line arguments are visible to other users in the process list.
              --image-width <n>   Width in pixels to request for images, 50 to 4000 (default 1400).
              --remote-images     Link to images on Medium instead of downloading them.
              --no-frontmatter    Omit the YAML frontmatter (a byline is printed instead).
          -h, --help              Show this help.

        Images are saved to <output-name>_assets/ next to the .md file. With "-o -" they stay remote links.

        Exit codes: 0 success, 1 the article could not be fetched or written, 2 bad usage,
                    70 internal error (a bug), 130 interrupted.
        """;

    /// <summary>A cookie header value: printable ASCII only. Anything else would end up raw in an HTTP header.</summary>
    public static bool IsValidCookieHeader(string value) => value.All(c => c is >= ' ' and <= '~');

    /// <exception cref="ArgumentException">The arguments are not valid; the message says why.</exception>
    public static CommandLineOptions Parse(IReadOnlyList<string> argv)
    {
        string? url = null, output = null, cookies = null;
        var width = DefaultImageWidth;
        bool remote = false, frontmatter = true, force = false, help = false;
        var seen = new HashSet<string>();

        // Takes the next argument as an option's value. A value that looks like another option is almost certainly a
        // forgotten value ("-o --remote-images"), so it is rejected rather than used as a file name.
        string Value(ref int i, string name)
        {
            if (!seen.Add(name)) throw new ArgumentException($"{name} was given more than once");
            if (i + 1 >= argv.Count) throw new ArgumentException($"{name} needs a value");
            var value = argv[++i];
            if (string.IsNullOrWhiteSpace(value) && name == "--output") throw new ArgumentException("--output needs a value, but it is empty");
            if (name == "--cookies" && !IsValidCookieHeader(value)) throw new ArgumentException("--cookies must be printable ASCII without control characters or line breaks");
            if (value.Length > 1 && value[0] == '-' && !(name == "--output" && value == "-"))
                throw new ArgumentException($"{name} needs a value, but the next argument is the option {value}");
            return value;
        }

        for (var i = 0; i < argv.Count; i++)
        {
            switch (argv[i])
            {
                case "-h" or "--help": help = true; break;
                case "-o" or "--output": output = Value(ref i, "--output"); break;
                case "-f" or "--force": force = true; break;
                case "--cookies": cookies = Value(ref i, "--cookies"); break;
                case "--image-width":
                    if (!int.TryParse(Value(ref i, "--image-width"), out width) || width is < 50 or > 4000)
                        throw new ArgumentException("--image-width must be a number between 50 and 4000");
                    break;
                case "--remote-images": remote = true; break;
                case "--no-frontmatter": frontmatter = false; break;
                case var a when a.Length > 1 && a[0] == '-': throw new ArgumentException($"unknown option {a}");
                default:
                    if (url is not null) throw new ArgumentException("only one URL can be given");
                    url = argv[i];
                    break;
            }
        }
        return new CommandLineOptions(url, output, cookies, width, remote, frontmatter, force, help);
    }
}
