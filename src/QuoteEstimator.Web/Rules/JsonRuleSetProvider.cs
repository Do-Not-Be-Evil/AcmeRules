using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace QuoteEstimator.Web.Rules;

public interface IRuleSetProvider
{
    /// <summary>
    /// The current rule set, or null when no valid configuration is loaded.
    /// Callers take one snapshot per request so a reload mid-request cannot mix rule versions.
    /// </summary>
    RuleSet? GetCurrent();
}

public sealed class RulesOptions
{
    public const string Section = "Rules";

    /// <summary>Path to the rule file, relative to the content root.</summary>
    public string Path { get; set; } = "Config/rules.json";
}

/// <summary>
/// Loads the rule set from a JSON file and swaps in a new immutable snapshot when the file changes.
/// A missing or invalid file leaves generation unavailable rather than falling back to stale rules.
/// </summary>
public sealed class JsonRuleSetProvider : IRuleSetProvider, IDisposable
{
    private readonly string _path;
    private readonly ILogger<JsonRuleSetProvider> _logger;
    private readonly PhysicalFileProvider? _fileProvider;
    private readonly IDisposable? _watch;
    private RuleSet? _current;

    public JsonRuleSetProvider(string path, ILogger<JsonRuleSetProvider> logger, bool watchForChanges = true)
    {
        _path = System.IO.Path.GetFullPath(path);
        _logger = logger;

        Reload();

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (watchForChanges && Directory.Exists(directory))
        {
            _fileProvider = new PhysicalFileProvider(directory);
            var fileName = System.IO.Path.GetFileName(_path);
            _watch = ChangeToken.OnChange(() => _fileProvider.Watch(fileName), Reload);
        }
    }

    public RuleSet? GetCurrent() => Volatile.Read(ref _current);

    private void Reload()
    {
        RuleSet? next = null;
        try
        {
            if (!File.Exists(_path))
            {
                _logger.LogError("Rule configuration not found at {Path}; quote generation is unavailable.", _path);
            }
            else
            {
                var result = RuleSetLoader.Parse(File.ReadAllText(_path));
                if (result.IsValid)
                {
                    next = result.RuleSet;
                    _logger.LogInformation("Loaded rule set version {Version} from {Path}.", next!.Version, _path);
                }
                else
                {
                    foreach (var error in result.Errors)
                        _logger.LogError("Invalid rule configuration in {Path}: {Error}", _path, error);
                    _logger.LogError("Quote generation is unavailable until the rule configuration is fixed.");
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not read rule configuration at {Path}; quote generation is unavailable.", _path);
        }

        Volatile.Write(ref _current, next);
    }

    public void Dispose()
    {
        _watch?.Dispose();
        _fileProvider?.Dispose();
    }
}
