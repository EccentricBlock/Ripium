using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ripium.Medium;

// DTOs for the subset of Medium's GraphQL `post` object that we render (a member nothing reads is not declared;
// see docs/medium-api.md for the full response). Every member is optional because Medium returns null for many
// fields; unknown members are ignored.

public sealed class Post
{
    public string? Title { get; set; }
    public string? MediumUrl { get; set; }
    public string? CanonicalUrl { get; set; }
    public double? ReadingTime { get; set; }
    public long? FirstPublishedAt { get; set; }
    public long? UpdatedAt { get; set; }
    public PreviewContent? PreviewContent { get; set; }
    public ImageRef? PreviewImage { get; set; }
    public Creator? Creator { get; set; }
    public Collection? Collection { get; set; }
    public List<Tag>? Tags { get; set; }
    public PostContent? Content { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 512, // reference expansion can nest entities; the default of 64 is for hand-written documents
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static Post FromJson(JsonNode post)
    {
        try
        {
            var result = post.Deserialize<Post>(Options) ?? throw new MediumException("Medium returned an empty post");
            result.RemoveNullElements();
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new MediumException($"Medium's response has an unexpected shape: {ex.Message}", ex);
        }
    }

    [JsonIgnore]
    public IReadOnlyList<Paragraph> Paragraphs => Content?.BodyModel?.Paragraphs ?? [];

    // JSON arrays may contain nulls ("tags":[null]); the renderer assumes every element is present.
    private void RemoveNullElements()
    {
        Tags?.RemoveAll(t => t is null);
        Content?.BodyModel?.Paragraphs?.RemoveAll(p => p is null);
        foreach (var paragraph in Content?.BodyModel?.Paragraphs ?? [])
            paragraph.Markups?.RemoveAll(m => m is null);
    }
}

public sealed class PreviewContent { public string? Subtitle { get; set; } }
public sealed class ImageRef { public string? Id { get; set; } }

public sealed class Creator
{
    public string? Name { get; set; }
    public string? Username { get; set; }
}

public sealed class Collection { public string? Name { get; set; } }

public sealed class Tag { public string? DisplayTitle { get; set; } }

public sealed class PostContent { public BodyModel? BodyModel { get; set; } }
public sealed class BodyModel { public List<Paragraph>? Paragraphs { get; set; } }

public sealed class Paragraph
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public string? Text { get; set; }
    public string? Href { get; set; }
    public List<Markup>? Markups { get; set; }
    public ImageMetadata? Metadata { get; set; }
    public IframeData? Iframe { get; set; }
    public MixtapeMetadata? MixtapeMetadata { get; set; }
    public CodeBlockMetadata? CodeBlockMetadata { get; set; }
}

public sealed class Markup
{
    public string? Type { get; set; }
    public int? Start { get; set; }
    public int? End { get; set; }
    public string? Href { get; set; }
    public string? AnchorType { get; set; }
    public string? UserId { get; set; }
}

public sealed class ImageMetadata
{
    public string? Id { get; set; }
    public string? Alt { get; set; }
}

public sealed class IframeData { public MediaResource? MediaResource { get; set; } }

public sealed class MediaResource
{
    public string? Id { get; set; }
    public string? IframeSrc { get; set; }
    public string? Title { get; set; }
}

public sealed class MixtapeMetadata
{
    public string? Href { get; set; }
}

public sealed class CodeBlockMetadata { public string? Lang { get; set; } }
