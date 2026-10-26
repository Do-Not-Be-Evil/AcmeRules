namespace QuoteEstimator.Tests.Admin;

/// <summary>A private copy of the shipped rule file, so admin tests never modify the real one.</summary>
internal sealed class TempRuleFile : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("rules-admin-").FullName;

    public TempRuleFile(string? content = null)
    {
        Path = System.IO.Path.Combine(_directory, "rules.json");
        if (content is not null)
            File.WriteAllText(Path, content);
        else
            File.Copy(ShippedRules.FilePath, Path);
    }

    public string Path { get; }

    public string Text => File.ReadAllText(Path);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
