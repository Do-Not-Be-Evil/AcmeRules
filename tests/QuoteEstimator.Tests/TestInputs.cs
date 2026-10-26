using System.Text.Json;

namespace QuoteEstimator.Tests;

internal static class TestInputs
{
    /// <summary>Builds evaluator inputs the way the API receives them, from an anonymous object.</summary>
    public static Dictionary<string, JsonElement> From(object values) =>
        JsonSerializer.SerializeToElement(values)
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());

    public static Dictionary<string, JsonElement> FromJson(JsonElement obj) =>
        obj.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
