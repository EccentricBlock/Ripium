using Ripium.Cli;

namespace Ripium.Tests;

public class CommandLineTests
{
    private static CommandLineOptions Parse(params string[] args) => CommandLine.Parse(args);

    [Fact]
    public void Defaults()
    {
        var o = Parse("https://medium.com/p/27832c8f6644");
        Assert.Equal("https://medium.com/p/27832c8f6644", o.Url);
        Assert.Null(o.Output);
        Assert.Null(o.Cookies);
        Assert.Equal(1400, o.ImageWidth);
        Assert.False(o.RemoteImages);
        Assert.True(o.Frontmatter);
        Assert.False(o.Force);
        Assert.False(o.Help);
    }

    [Fact]
    public void AllOptions_InAnyOrder()
    {
        var o = Parse("--remote-images", "-o", "out/a.md", "id12345678ab", "--no-frontmatter", "--force", "--cookies", "uid=1", "--image-width", "700");
        Assert.Equal("id12345678ab", o.Url);
        Assert.Equal("out/a.md", o.Output);
        Assert.Equal("uid=1", o.Cookies);
        Assert.Equal(700, o.ImageWidth);
        Assert.True(o.RemoteImages);
        Assert.False(o.Frontmatter);
        Assert.True(o.Force);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Help(string flag) => Assert.True(Parse(flag).Help);

    [Fact]
    public void ShortAliases() => Assert.True(Parse("-f", "x").Force);

    [Fact]
    public void DashMeansStdout_ButOnlyAsTheOutputValue()
    {
        Assert.Equal("-", Parse("x", "-o", "-").Output);
        Assert.Equal("-", Parse("x", "--output", "-").Output);
    }

    [Theory]
    [InlineData("-o")]
    [InlineData("--output")]
    [InlineData("--cookies")]
    [InlineData("--image-width")]
    public void OptionWithoutValue_IsRejected(string option) =>
        Assert.Contains("needs a value", Assert.Throws<ArgumentException>(() => Parse("x", option)).Message);

    [Theory]
    [InlineData("-o", "--remote-images")]
    [InlineData("--output", "-f")]
    [InlineData("--cookies", "--force")]
    public void OptionLikeValue_IsRejected_NotUsedAsAFileName(string option, string value) =>
        Assert.Contains("is the option", Assert.Throws<ArgumentException>(() => Parse("x", option, value)).Message);

    [Theory]
    [InlineData("-o", "a", "-o", "b")]
    [InlineData("--output", "a", "-o", "b")]
    [InlineData("--cookies", "a", "--cookies", "b")]
    public void RepeatedValueOptions_AreRejected(params string[] args) =>
        Assert.Contains("more than once", Assert.Throws<ArgumentException>(() => Parse(["x", .. args])).Message);

    [Theory]
    [InlineData("--image-width", "49")]
    [InlineData("--image-width", "4001")]
    [InlineData("--image-width", "wide")]
    [InlineData("--image-width", "")]
    [InlineData("--image-width", "1e3")]
    public void BadImageWidth_IsRejected(string option, string value) =>
        Assert.Contains("between 50 and 4000", Assert.Throws<ArgumentException>(() => Parse("x", option, value)).Message);

    [Theory]
    [InlineData("50")]
    [InlineData("4000")]
    public void ImageWidthBoundaries_AreAccepted(string width) => Assert.Equal(int.Parse(width), Parse("x", "--image-width", width).ImageWidth);

    [Theory]
    [InlineData("-o")]
    [InlineData("--output")]
    public void EmptyOutput_IsRejected_NotSilentlyIgnored(string option) =>
        Assert.Contains("empty", Assert.Throws<ArgumentException>(() => Parse("x", option, "")).Message);

    [Fact]
    public void UnknownOption_IsRejected() =>
        Assert.Contains("unknown option --bogus", Assert.Throws<ArgumentException>(() => Parse("x", "--bogus")).Message);

    [Fact]
    public void TwoUrls_AreRejected() =>
        Assert.Contains("only one URL", Assert.Throws<ArgumentException>(() => Parse("a", "b")).Message);

    [Fact]
    public void NoArguments_ParsesToNoUrl() => Assert.Null(Parse().Url);

    [Fact]
    public void ASingleDashIsNotAnOption_AndNotAUrl() =>
        Assert.Equal("-", Parse("-").Url); // reaches URL resolution, which rejects it with a clear message

    [Fact]
    public void UsageMentionsEveryOption()
    {
        foreach (var option in new[] { "--output", "--force", "--cookies", "--image-width", "--remote-images", "--no-frontmatter", "--help" })
            Assert.Contains(option, CommandLine.Usage);
        Assert.Contains("70", CommandLine.Usage);
    }
}
