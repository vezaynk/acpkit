using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace AcpKit.Conformance;

/// <summary>
/// Round-trips every payload in the generated corpus through the generated contracts.
/// </summary>
/// <remarks>
/// <para>
/// This is the coverage gate. The corpus is derived from the schema, so a type that exists is
/// a type that gets exercised — including every union arm, every open-enum member, and a
/// vendor extension value per enum. Hand-written fixtures cover the types someone thought of;
/// this covers the ones nobody did, which is where a generator's mistakes actually live.
/// </para>
/// <para>
/// The assertion is that a payload survives being read and written: every property the schema
/// declares comes back. A type whose converter silently drops a field fails here, which no
/// amount of compiling would have caught.
/// </para>
/// </remarks>
internal static class CorpusScenarios
{
    /// <summary>A protocol version's catalog, supplied by the generated assemblies.</summary>
    internal sealed record Catalog(string Name, string[] TypeNames, Func<string, JsonTypeInfo?> Find);

    public static void Register(Runner runner, IReadOnlyList<Catalog> catalogs, string corpusDirectory)
    {
        foreach (var catalog in catalogs)
        {
            var path = Path.Combine(corpusDirectory, $"{catalog.Name}.json");
            runner.Add("corpus", $"{catalog.Name}: every generated type round-trips",
                _ => RoundTripAll(catalog, path));
            runner.Add("corpus", $"{catalog.Name}: every declared type has a sample",
                _ => EveryTypeCovered(catalog, path));

            if (catalog.Name == "v2-stable")
            {
                // Only the line the protocol scenarios actually drive. Asserting method
                // coverage for a line nothing exercises would be a test of the waiver list.
                runner.Add("corpus", $"{catalog.Name}: every method is exercised or waived",
                    _ => EveryMethodExercised(path));
            }
        }
    }

    private static Task RoundTripAll(Catalog catalog, string path)
    {
        var corpus = LoadCorpus(path);
        var failures = new List<string>();
        var checked_ = 0;

        foreach (var sample in corpus["samples"]!.AsArray())
        {
            var label = sample!["label"]!.GetValue<string>();
            var typeName = sample["type"]!.GetValue<string>();
            var payload = sample["payload"]!;

            var typeInfo = catalog.Find(typeName);
            if (typeInfo is null)
            {
                failures.Add($"{label}: the catalog does not know {typeName}");
                continue;
            }

            try
            {
                var decoded = JsonSerializer.Deserialize(payload.ToJsonString(), typeInfo);
                if (decoded is null)
                {
                    failures.Add($"{label}: decoded to null");
                    continue;
                }

                var reencoded = JsonSerializer.Serialize(decoded, typeInfo);
                var lost = MissingMembers(payload, JsonNode.Parse(reencoded));
                if (lost.Count > 0)
                {
                    failures.Add($"{label}: lost {string.Join(", ", lost.Take(4))}");
                }

                checked_++;
            }
            catch (Exception e)
            {
                failures.Add($"{label}: {e.GetType().Name}: {e.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"{failures.Count} of {failures.Count + checked_} samples failed:\n      "
                + string.Join("\n      ", failures.Take(12)));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Every type the generated assembly declares must appear in the corpus.
    /// </summary>
    /// <remarks>
    /// The half of the gate that catches omission rather than breakage: a type that emits fine
    /// but that nothing ever reads is protocol surface shipped untested.
    /// </remarks>
    private static Task EveryTypeCovered(Catalog catalog, string path)
    {
        var corpus = LoadCorpus(path);
        var covered = corpus["samples"]!.AsArray()
            .Select(s => s!["type"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

        var missing = catalog.TypeNames.Where(n => !covered.Contains(n)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"{missing.Count} declared type(s) have no sample: {string.Join(", ", missing.Take(10))}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Methods the scenarios do not yet drive, each with the reason it is outstanding.
    /// </summary>
    /// <remarks>
    /// Listed rather than omitted. An uncovered method is a real gap, and the only thing worse
    /// than having one is not being able to see it — a silent gap is indistinguishable from
    /// coverage until an agent calls the method in production.
    /// </remarks>
    private static readonly Dictionary<string, string> Waived = new(StringComparer.Ordinal)
    {
        ["auth/login"] = "needs an agent that refuses session/new with -32000 first",
        ["auth/logout"] = "paired with auth/login",
        ["session/list"] = "no multi-session fixture yet",
        ["session/delete"] = "optional capability; needs a fixture that advertises it",
        ["session/resume"] = "needs a replayFrom fixture",
        ["session/close"] = "needs a lifecycle fixture beyond a single turn",
        ["session/set_config_option"] = "needs a fixture advertising config options",
        ["elicitation/create"] = "client-answered; needs an elicitation fixture",
    };

    private static Task EveryMethodExercised(string path)
    {
        var corpus = LoadCorpus(path);
        var declared = corpus["methods"]!.AsArray()
            .Select(m => m!["path"]!.GetValue<string>())
            .ToList();

        var seen = MethodTap.Methods;
        var uncovered = declared.Where(m => !seen.Contains(m) && !Waived.ContainsKey(m)).ToList();

        if (uncovered.Count > 0)
        {
            throw new InvalidOperationException(
                $"{uncovered.Count} method(s) neither exercised nor waived: {string.Join(", ", uncovered)}");
        }

        // A waiver that is no longer needed is stale, and stale waivers are how a coverage gate
        // quietly stops meaning anything.
        var redundant = Waived.Keys.Where(seen.Contains).ToList();
        if (redundant.Count > 0)
        {
            throw new InvalidOperationException(
                $"{redundant.Count} waiver(s) are now covered and should be removed: {string.Join(", ", redundant)}");
        }

        return Task.CompletedTask;
    }

    private static JsonObject LoadCorpus(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The corpus is missing. Run 'dotnet run --project tools/AcpKit.Generator -- generate'.", path);
        }

        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    /// <summary>
    /// Property paths present in the original payload but absent after a round trip.
    /// </summary>
    /// <remarks>
    /// Compares presence rather than equality. A round trip is allowed to normalise — reorder
    /// members, drop a property whose value equals its default — but it is not allowed to lose
    /// a value the sender supplied.
    /// </remarks>
    private static List<string> MissingMembers(JsonNode? original, JsonNode? actual, string prefix = "")
    {
        var lost = new List<string>();

        if (original is JsonObject source)
        {
            if (actual is not JsonObject target)
            {
                lost.Add(prefix.Length == 0 ? "(the whole object)" : prefix);
                return lost;
            }

            foreach (var (key, value) in source)
            {
                var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
                if (!target.TryGetPropertyValue(key, out var round))
                {
                    lost.Add(path);
                    continue;
                }

                lost.AddRange(MissingMembers(value, round, path));
            }

            return lost;
        }

        if (original is JsonArray items)
        {
            if (actual is not JsonArray roundTripped || roundTripped.Count < items.Count)
            {
                lost.Add(prefix.Length == 0 ? "(the whole array)" : prefix);
                return lost;
            }

            for (var i = 0; i < items.Count; i++)
            {
                lost.AddRange(MissingMembers(items[i], roundTripped[i], $"{prefix}[{i}]"));
            }
        }

        return lost;
    }
}
