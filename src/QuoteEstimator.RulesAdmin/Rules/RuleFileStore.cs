using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuoteEstimator.RulesAdmin.Rules;

public sealed class RulesOptions
{
    public const string Section = "Rules";

    /// <summary>Path to the rule file the quote app reads, relative to the content root.</summary>
    public string Path { get; set; } = "../QuoteEstimator.Web/Config/rules.json";
}

/// <summary>The rule file as it is on disk right now.</summary>
/// <param name="Etag">Hash of the file bytes; every change must name the snapshot it was based on.</param>
/// <param name="Load">The strict parse, used for display. Invalid or unreadable files carry their errors.</param>
/// <param name="Document">The raw JSON (comments stripped), or null when it is not readable JSON.</param>
public sealed record RuleFileSnapshot(string? Etag, RuleSetLoadResult Load, JsonObject? Document)
{
    public bool CanEdit => Load.IsValid && Document is not null;
}

/// <summary>The outcome of a change. Only <see cref="Saved"/> means the file was written.</summary>
public abstract record RuleFileChange
{
    private RuleFileChange() { }

    public sealed record Saved(string Version, string Etag) : RuleFileChange;

    /// <summary>The file changed after the editor loaded it.</summary>
    public sealed record Stale : RuleFileChange;

    public sealed record InvalidVersion(string Message) : RuleFileChange;

    public sealed record NotFound(string RuleId) : RuleFileChange;

    public sealed record DuplicateId(string RuleId) : RuleFileChange;

    public sealed record InvalidRequest(string Message) : RuleFileChange;

    /// <summary>The changed rule set fails <see cref="RuleSetLoader"/> validation; nothing was written.</summary>
    public sealed record Invalid(IReadOnlyList<string> Errors) : RuleFileChange;

    /// <summary>The current file cannot be read or is not valid, so it cannot be edited safely.</summary>
    public sealed record Unavailable(IReadOnlyList<string> Errors) : RuleFileChange;
}

/// <summary>
/// The only code that reads or writes the rule file. Every change re-reads the file, checks the
/// caller's ETag, requires a new version, validates the whole result with <see cref="RuleSetLoader"/>
/// and replaces the file atomically, so the quote app never sees a half-written or invalid file.
/// </summary>
public sealed class RuleFileStore(string path)
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        // The file is read by our own loader, not embedded in HTML; keep apostrophes and symbols readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public RuleFileSnapshot Read()
    {
        _gate.Wait();
        try
        {
            return ReadUnlocked().Snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public RuleFileChange Create(string? expectedEtag, string? newVersion, JsonObject? rule) =>
        Mutate(expectedEtag, newVersion, rules =>
        {
            if (RuleId(rule) is not { } id)
                return new RuleFileChange.InvalidRequest("The rule needs an id.");
            if (IndexOf(rules, id) >= 0)
                return new RuleFileChange.DuplicateId(id);
            rules.Add(rule!);
            return null;
        });

    public RuleFileChange Update(string? expectedEtag, string? newVersion, string ruleId, JsonObject? rule) =>
        Mutate(expectedEtag, newVersion, rules =>
        {
            if (RuleId(rule) is not { } id)
                return new RuleFileChange.InvalidRequest("The rule needs an id.");
            var index = IndexOf(rules, ruleId);
            if (index < 0)
                return new RuleFileChange.NotFound(ruleId);
            if (id != ruleId && IndexOf(rules, id) >= 0)
                return new RuleFileChange.DuplicateId(id);
            rules[index] = rule!;
            return null;
        });

    public RuleFileChange Delete(string? expectedEtag, string? newVersion, string ruleId) =>
        Mutate(expectedEtag, newVersion, rules =>
        {
            var index = IndexOf(rules, ruleId);
            if (index < 0)
                return new RuleFileChange.NotFound(ruleId);
            rules.RemoveAt(index);
            return null;
        });

    /// <param name="edit">Changes the rules array, or returns why it cannot.</param>
    private RuleFileChange Mutate(string? expectedEtag, string? newVersion, Func<JsonArray, RuleFileChange?> edit)
    {
        _gate.Wait();
        try
        {
            var (snapshot, header) = ReadUnlocked();
            if (!snapshot.CanEdit)
                return new RuleFileChange.Unavailable(snapshot.Load.Errors);
            if (expectedEtag != snapshot.Etag)
                return new RuleFileChange.Stale();

            var document = snapshot.Document!;
            var currentVersion = snapshot.Load.RuleSet!.Version;
            var version = newVersion?.Trim();
            if (string.IsNullOrEmpty(version))
                return new RuleFileChange.InvalidVersion("Enter a new rule set version.");
            if (version == currentVersion)
                return new RuleFileChange.InvalidVersion($"The new version must differ from the current version '{currentVersion}'.");

            if (document["rules"] is not JsonArray rules)
                return new RuleFileChange.Unavailable(["The rule file has no rules array."]);
            if (edit(rules) is { } refused)
                return refused;
            document["version"] = version;

            var json = document.ToJsonString(WriteOptions);
            var check = RuleSetLoader.Parse(json);
            if (!check.IsValid)
                return new RuleFileChange.Invalid(check.Errors);

            var bytes = Encoding.UTF8.GetBytes(header + json + Environment.NewLine);
            WriteAtomically(bytes);
            return new RuleFileChange.Saved(version, Hash(bytes));
        }
        finally
        {
            _gate.Release();
        }
    }

    private (RuleFileSnapshot Snapshot, string Header) ReadUnlocked()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new(null, new(null, [$"Could not read the rule file at {Path}: {ex.Message}"]), null), "");
        }

        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        var load = RuleSetLoader.Parse(text);
        var (header, body) = SplitHeader(text);
        JsonObject? document = null;
        try
        {
            document = JsonNode.Parse(body, documentOptions: ReadOptions) as JsonObject;
        }
        catch (JsonException)
        {
            // The load errors already explain why the file is not usable.
        }

        return (new(Hash(bytes), load, document), header);
    }

    /// <summary>Keeps the leading comment block (the placeholder warning) so it survives a rewrite.</summary>
    private static (string Header, string Body) SplitHeader(string text)
    {
        var position = 0;
        while (position < text.Length)
        {
            var lineEnd = text.IndexOf('\n', position);
            var next = lineEnd < 0 ? text.Length : lineEnd + 1;
            var line = text[position..next].Trim();
            if (line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
                break;
            position = next;
        }
        return (text[..position], text[position..]);
    }

    private void WriteAtomically(byte[] bytes)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        var temp = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static string? RuleId(JsonObject? rule) =>
        rule?["id"] is JsonValue value && value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;

    private static int IndexOf(JsonArray rules, string id)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            if (RuleId(rules[i] as JsonObject) == id)
                return i;
        }
        return -1;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
