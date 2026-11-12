namespace Ripium.Markdown;

public static class Timestamps
{
    private const long MinUnixMilliseconds = -62_135_596_800_000L; // 0001-01-01T00:00:00Z
    private const long MaxUnixMilliseconds = 253_402_300_799_999L; // 9999-12-31T23:59:59.999Z

    /// <summary>Null instead of an exception when Medium sends a value outside the range <see cref="DateTimeOffset"/> can hold.</summary>
    public static DateTimeOffset? TryFromUnixMilliseconds(long milliseconds) =>
        milliseconds is >= MinUnixMilliseconds and <= MaxUnixMilliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;
}
