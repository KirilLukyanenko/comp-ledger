#:property PublishAot=false

// Prints metrics/runs.jsonl as a Markdown table, for the CI job summary and the README.
//
//   dotnet run tools/metrics-report.cs [-- path/to/runs.jsonl]
//
// Every run is listed, failed ones included. Cost is the estimate Claude Code reports at API list
// prices (total_cost_usd); on a subscription it is not what was billed.

using System.Globalization;
using System.Text.Json.Nodes;

var file = args.Length > 0 ? args[0] : Path.Combine("metrics", "runs.jsonl");
if (!File.Exists(file))
{
    Console.WriteLine($"No agent runs recorded yet ({file} not found).");
    return 0;
}

var runs = File.ReadLines(file)
    .Where(l => !string.IsNullOrWhiteSpace(l))
    .Select(l => JsonNode.Parse(l)!.AsObject())
    .Where(r => r["replay"]?.GetValue<bool>() != true)
    .ToList();

var inv = CultureInfo.InvariantCulture;
string Str(JsonNode? n) => n?.ToString() ?? "";
double Num(JsonNode? n) => n is null ? 0 : n.GetValue<double>();

static bool AllPass(JsonNode? gates) =>
    gates is JsonObject g && g.Count > 0 && g.All(p => p.Value?.ToString() == "pass");

Console.WriteLine("### Agent runs");
Console.WriteLine();
Console.WriteLine("| run | PRP | attempt | started (UTC) | turns | minutes | cost, USD | commits | gates |");
Console.WriteLine("|---|---|---|---|---|---|---|---|---|");
foreach (var r in runs)
{
    var agent = r["agent"];
    var gates = r["gates"] as JsonObject;
    var gateText = gates is null ? "" : string.Join(" ", gates.Select(p => $"{p.Key}:{p.Value}"));
    var started = DateTime.TryParse(Str(r["startedAt"]), inv, DateTimeStyles.AdjustToUniversal, out var s) ? s.ToString("yyyy-MM-dd HH:mm", inv) : "";
    Console.WriteLine(string.Join(" | ", [
        $"| `{Str(r["runId"])}`",
        $"{Str(r["prp"])} v{Str(r["prpVersion"])}",
        Str(r["attempt"]),
        started,
        Str(agent?["numTurns"]),
        (Num(agent?["durationMs"]) / 60000).ToString("0.0", inv),
        Num(agent?["costUsd"]).ToString("0.00", inv),
        Str(r["commits"]),
        $"{gateText} |",
    ]));
}

var passed = runs.Count(r => AllPass(r["gates"]));
var cost = runs.Sum(r => Num(r["agent"]?["costUsd"]));
Console.WriteLine();
Console.WriteLine($"{runs.Count} runs, {passed} with all gates passing. Total estimated cost: ${cost.ToString("0.00", inv)} at API list prices.");
Console.WriteLine("Holdout scenarios and the load gate run in the `holdout` job, outside the agent's view.");
return 0;
