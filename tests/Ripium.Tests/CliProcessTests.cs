using System.Diagnostics;

namespace Ripium.Tests;

/// <summary>
/// Runs the real executable. Network failures are simulated with an unreachable proxy so no test touches Medium,
/// and the cases that used to crash with an unhandled exception stay fixed.
/// </summary>
public sealed class CliProcessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ripium-cli-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private record Result(int ExitCode, string Stdout, string Stderr);

    private async Task<Result> Run(string[] args, bool deadNetwork = true, Dictionary<string, string>? environment = null)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "ripium.dll");
        Assert.True(File.Exists(dll), $"{dll} is missing; the test project must reference the app");

        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _dir };
        start.ArgumentList.Add(dll);
        foreach (var a in args) start.ArgumentList.Add(a);
        if (deadNetwork)
        {
            foreach (var name in new[] { "https_proxy", "HTTPS_PROXY", "http_proxy", "HTTP_PROXY", "all_proxy", "ALL_PROXY" })
                start.Environment[name] = "http://127.0.0.1:9"; // discard port: connection refused immediately
            start.Environment["MEDIUM_AUTH_COOKIES"] = "";
        }
        foreach (var (key, value) in environment ?? []) start.Environment[key] = value;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return new Result(process.ExitCode, await stdout, await stderr);
    }

    [Fact]
    public async Task Help_PrintsUsage_AndSucceeds()
    {
        var r = await Run(["--help"]);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Usage:", r.Stdout);
        Assert.Contains("--force", r.Stdout);
    }

    [Fact]
    public async Task NoArguments_IsAUsageError()
    {
        var r = await Run([]);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("Usage:", r.Stderr);
        Assert.Empty(r.Stdout);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-o")]
    public async Task BadArguments_AreUsageErrors_WithTheReason(string argument)
    {
        var r = await Run(["27832c8f6644", argument]);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("error:", r.Stderr);
    }

    [Fact]
    public async Task AnOptionAsTheOutputValue_IsNotWrittenAsAFileName()
    {
        var r = await Run(["27832c8f6644", "-o", "--remote-images"]);
        Assert.Equal(2, r.ExitCode);
        Assert.Empty(Directory.GetFileSystemEntries(_dir)); // the bug created a file literally named "--remote-images"
    }

    [Fact]
    public async Task NotAMediumUrl_ExitsWithAClearError()
    {
        var r = await Run(["https://example.com/nothing"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("Could not find a Medium post id", r.Stderr);
        Assert.DoesNotContain("Unhandled", r.Stderr);
    }

    [Fact]
    public async Task ExistingOutputFile_IsRefused_BeforeAnyNetworkAccess()
    {
        var existing = Path.Combine(_dir, "mine.md");
        await File.WriteAllTextAsync(existing, "precious");

        var r = await Run(["27832c8f6644", "-o", existing]);

        Assert.Equal(1, r.ExitCode);
        Assert.Contains("already exists", r.Stderr);
        Assert.DoesNotContain("Fetching", r.Stderr); // refused up front, not after a download
        Assert.Equal("precious", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task ExistingOutputFile_WithoutExtension_IsRefusedToo()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "mine.md"), "precious");
        var r = await Run(["27832c8f6644", "-o", Path.Combine(_dir, "mine")]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("already exists", r.Stderr);
    }

    [Fact]
    public async Task Force_GetsPastTheExistingFileCheck()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "mine.md"), "old");
        var r = await Run(["27832c8f6644", "-o", Path.Combine(_dir, "mine.md"), "--force"]);
        Assert.Contains("Fetching", r.Stderr); // reached the network step (which then fails: the proxy is dead)
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(_dir, "mine.md"))); // nothing written on failure
    }

    [Fact]
    public async Task NetworkFailure_IsACleanError_NotACrash()
    {
        var r = await Run(["27832c8f6644"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("Failed to fetch post 27832c8f6644", r.Stderr);
        Assert.DoesNotContain("Unhandled", r.Stderr);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public async Task ShortLinkNetworkFailure_IsACleanError_NotACrash()
    {
        // this used to escape as an unhandled HttpRequestException (exit code 134)
        var r = await Run(["https://link.medium.com/abc123"]);
        Assert.Equal(1, r.ExitCode);
        Assert.StartsWith("error:", r.Stderr.Trim());
        Assert.DoesNotContain("Unhandled", r.Stderr);
    }

    [Fact]
    public async Task AControlSequenceInAnArgument_IsNotEchoedToTheTerminal()
    {
        var r = await Run(["--\u001b[31mbad"]);
        Assert.Equal(2, r.ExitCode);
        Assert.DoesNotContain('\u001b', r.Stderr);
    }

    [Theory]
    [InlineData("uid=1\r\nX-Evil: 1")]
    [InlineData("uid=\u00e9")]
    public async Task CookiesFromTheEnvironment_AreValidatedLikeTheFlag(string cookies)
    {
        var r = await Run(["27832c8f6644"], deadNetwork: true, environment: new() { ["MEDIUM_AUTH_COOKIES"] = cookies });
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("MEDIUM_AUTH_COOKIES", r.Stderr);
        Assert.DoesNotContain("Fetching", r.Stderr);
    }
}
