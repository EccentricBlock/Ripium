using Ripium.Output;

namespace Ripium.Tests;

public sealed class OutputPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ripium-paths-").FullName;
    private readonly string _previousDirectory = Directory.GetCurrentDirectory();

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_previousDirectory);
        Directory.Delete(_root, recursive: true);
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    [Fact]
    public void NoOutput_UsesTheDefaultNameInTheCurrentDirectory()
    {
        Directory.SetCurrentDirectory(_root);
        Assert.Equal(Path.GetFullPath("my-title.md"), OutputPaths.Resolve(null, "my-title", force: false));
        Assert.Equal(Path.GetFullPath("my-title.md"), OutputPaths.Resolve("", "my-title", force: false));
    }

    [Fact]
    public void ExistingDirectory_GetsTheDefaultNameInside()
    {
        Directory.CreateDirectory(P("notes"));
        Assert.Equal(P("notes", "my-title.md"), OutputPaths.Resolve(P("notes"), "my-title", false));
    }

    [Fact]
    public void TrailingSlash_MeansDirectory_AndIsCreated()
    {
        var path = OutputPaths.Resolve(P("new", "deep") + "/", "my-title", false);
        Assert.Equal(P("new", "deep", "my-title.md"), path);
        Assert.True(Directory.Exists(P("new", "deep")));
    }

    [Fact]
    public void FileWithExtension_IsUsedAsIs_AndItsFolderCreated()
    {
        Assert.Equal(P("a", "b", "story.markdown"), OutputPaths.Resolve(P("a", "b", "story.markdown"), "ignored", false));
        Assert.True(Directory.Exists(P("a", "b")));
    }

    [Fact]
    public void FileWithoutExtension_GetsDotMd() =>
        Assert.Equal(P("story.md"), OutputPaths.Resolve(P("story"), "ignored", false));

    [Fact]
    public void ExistingFile_IsRefused_ByDefault()
    {
        File.WriteAllText(P("keep.md"), "precious");
        var ex = Assert.Throws<IOException>(() => OutputPaths.Resolve(P("keep.md"), "x", force: false));
        Assert.Contains("--force", ex.Message);
        Assert.Equal("precious", File.ReadAllText(P("keep.md")));
    }

    [Fact]
    public void ExistingFile_IsAllowed_WithForce()
    {
        File.WriteAllText(P("keep.md"), "old");
        Assert.Equal(P("keep.md"), OutputPaths.Resolve(P("keep.md"), "x", force: true));
    }

    [Fact]
    public void RefusalAppliesToTheDefaultNameToo()
    {
        Directory.SetCurrentDirectory(_root);
        File.WriteAllText("my-title.md", "mine");
        Assert.Throws<IOException>(() => OutputPaths.Resolve(null, "my-title", false));
    }

    [Fact]
    public void ExtensionlessNameThatMatchesAnExistingFileWithDotMd_IsRefused()
    {
        File.WriteAllText(P("story.md"), "mine");
        Assert.Throws<IOException>(() => OutputPaths.Resolve(P("story"), "x", false));
    }

    [Fact]
    public void ADirectoryWhereTheFileShouldBe_IsAnError()
    {
        Directory.CreateDirectory(P("clash.md"));
        // an existing directory is treated as "put it inside", so the clash only arises for the extensionless form
        Assert.Equal(P("clash.md", "x.md"), OutputPaths.Resolve(P("clash.md"), "x", false));
        Directory.CreateDirectory(P("clash2.md"));
        Assert.Throws<IOException>(() => OutputPaths.EnsureWritable(P("clash2.md"), force: true));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("-", false)]
    [InlineData("out/", false)]
    [InlineData("out/story.md", true)]
    [InlineData("story", true)]
    public void NamesAFile(string? output, bool expected) => Assert.Equal(expected, OutputPaths.NamesAFile(output));

    [Fact]
    public void NamesAFile_IsFalseForAnExistingDirectory() => Assert.False(OutputPaths.NamesAFile(_root));

    [Fact]
    public void ExplicitFilePath_MatchesWhatResolveWouldUse()
    {
        Assert.Equal(P("story.md"), OutputPaths.ExplicitFilePath(P("story")));
        Assert.Equal(P("story.txt"), OutputPaths.ExplicitFilePath(P("story.txt")));
    }
}
