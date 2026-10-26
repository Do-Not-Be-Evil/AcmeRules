namespace QuoteEstimator.Tests;

/// <summary>The rule file shipped with the quote app, as copied into the test output.</summary>
internal static class ShippedRules
{
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "Config", "rules.json");

    /// <summary>Read from the file so tests keep passing when the version is bumped for an approved rule change.</summary>
    public static string Version => RuleSetLoader.Parse(File.ReadAllText(FilePath)).RuleSet!.Version;
}
