using System.Text.Json.Nodes;

namespace Ripium.Medium;

/// <summary>
/// Replaces Apollo-style <c>{"__ref": "Type:id"}</c> objects with the entity stored under that key in the
/// response's <c>data</c> object. Mirrors Freedium's <c>resolve_graphql_references</c>.
/// <para>
/// Normalised responses can contain reference cycles (a post refers to its author, who refers back to posts).
/// A reference is therefore left in place when the entity it points to is already being expanded higher up the
/// same branch. <see cref="MaxExpansions"/> bounds the total work for heavily shared graphs and
/// <see cref="MaxChainDepth"/> the nesting.
/// </para>
/// </summary>
public static class GraphQlRefResolver
{
    public const int MaxExpansions = 50_000;

    /// <summary>Longest chain of references followed inside one another; a hostile response could otherwise exhaust the stack.</summary>
    public const int MaxChainDepth = 24;

    /// <summary>
    /// Most JSON nodes copied in one call. <see cref="MaxExpansions"/> counts references, not size: 45,000 references to a
    /// 20 KB entity expanded to 900 MB (21 s, 3.6 GB of memory) from a 700 KB response.
    /// </summary>
    public const int MaxNodes = 1_000_000;

    /// <summary>
    /// Most serialised bytes of referenced entities expanded in one call. A node count cannot see size (a string is one node):
    /// 400 references to a 1 MB string allocated 1.5 GB.
    /// </summary>
    public const long MaxExpandedBytes = 64L * 1024 * 1024;

    /// <summary>Returns a deep-resolved copy of <paramref name="node"/>; <paramref name="lookup"/> is not modified.</summary>
    public static JsonNode? Resolve(JsonNode? node, JsonObject lookup) =>
        new Walker(lookup).Visit(node);

    private sealed class Walker(JsonObject lookup)
    {
        private readonly HashSet<string> _expanding = [];
        private int _expansions;
        private int _nodes;
        private long _bytes;
        private readonly Dictionary<string, int> _sizes = [];

        public JsonNode? Visit(JsonNode? node)
        {
            _nodes++;
            switch (node)
            {
                case JsonObject obj:
                    if (obj.Count == 1 && obj["__ref"] is JsonValue refValue && refValue.TryGetValue<string>(out var key))
                        return VisitRef(obj, key);

                    var copy = new JsonObject();
                    foreach (var (name, child) in obj)
                        copy[name] = Visit(child);
                    return copy;

                case JsonArray arr:
                    var list = new JsonArray();
                    foreach (var child in arr)
                        list.Add(Visit(child));
                    return list;

                default:
                    return node?.DeepClone();
            }
        }

        private JsonNode? VisitRef(JsonObject refObject, string key)
        {
            // unresolvable, cyclic or over budget: leave the reference visible instead of guessing
            if (lookup[key] is not { } target || _expanding.Contains(key) || _expansions >= MaxExpansions || _expanding.Count >= MaxChainDepth || _nodes >= MaxNodes)
                return refObject.DeepClone();

            // the size of an entity is measured once; each expansion then costs that much of the budget
            if (!_sizes.TryGetValue(key, out var size)) _sizes[key] = size = target.ToJsonString().Length;
            if (_bytes + size > MaxExpandedBytes) return refObject.DeepClone();
            _bytes += size;

            _expansions++;
            _expanding.Add(key);
            try
            {
                return Visit(target);
            }
            finally
            {
                _expanding.Remove(key);
            }
        }
    }
}
