#:property PublishAot=false

// Runs one agent attempt at a PRP and appends the result to metrics/runs.jsonl.
//
//   dotnet run tools/run-prp.cs -- <prp> [options]
//
// Options:
//   --model <alias|id>       model for the run (default: Claude Code's default)
//   --max-turns <n>          stop the agent after n turns
//   --max-budget-usd <x>     stop the agent at this estimated cost
//   --allow-draft            run even if the PRP status is not Ready (the run is marked as such)
//   --replay <file>          don't call Claude Code; use a saved `claude -p --output-format json` result.
//                            For testing this tool. The worktree and branch are removed afterwards.
//   --runs-file <path>       write the record here instead of metrics/runs.jsonl
//
// Environment:
//   CLAUDE_BIN      path to the Claude Code CLI (default: "claude" on PATH)
//   BASH_PATH       path to bash (default: Git Bash on Windows, "bash" elsewhere)
//   SCENARIOS_PATH  holdout scenarios checkout to deny to the agent (default: ../comp-ledger-scenarios)
//
// Steps: git worktree on a new branch -> claude -p "/implement-prp <prp> <runId>" -> tools/verify.sh
// in the worktree (run by this tool, not by the agent) -> one JSON line in the runs file.
// The worktree is kept for human review. Nothing is pushed.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var opt = Options.Parse(args);
if (opt is null)
{
    Console.Error.WriteLine("usage: dotnet run tools/run-prp.cs -- <prp> [--model m] [--max-turns n] [--max-budget-usd x] [--allow-draft] [--replay file] [--runs-file path]");
    return 2;
}

var repo = Proc.Git(Environment.CurrentDirectory, "rev-parse", "--show-toplevel").Trim();
var specs = Directory.GetFiles(Path.Combine(repo, "specs"), $"{opt.Prp}-*.prp.md");
if (specs.Length != 1)
{
    Console.Error.WriteLine($"expected exactly one spec matching specs/{opt.Prp}-*.prp.md, found {specs.Length}");
    return 2;
}
var specText = File.ReadAllText(specs[0]);
var status = Spec.Field(specText, "Status");
var version = Spec.Field(specText, "Version");
if (status != "Ready" && !opt.AllowDraft)
{
    Console.Error.WriteLine($"PRP-{opt.Prp} status is '{status}', not 'Ready'. Use --allow-draft to run anyway.");
    return 2;
}

var runsFile = opt.RunsFile ?? Path.Combine(repo, "metrics", "runs.jsonl");
var attempt = Runs.CountAttempts(runsFile, opt.Prp, version) + 1;
var runId = $"prp{opt.Prp}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid():N}"[..32];
var branch = $"agent/{runId}";
var worktree = Path.Combine(Path.GetDirectoryName(repo)!, "comp-ledger-worktrees", runId);
var baseCommit = Proc.Git(repo, "rev-parse", "HEAD").Trim();

Console.WriteLine($"run {runId}: PRP-{opt.Prp} v{version} ({status}), attempt {attempt}");
Proc.Git(repo, "worktree", "add", "-b", branch, worktree, baseCommit);
var runDir = Path.Combine(worktree, ".prp-run");
Directory.CreateDirectory(runDir);

var startedAt = DateTimeOffset.UtcNow;
string agentJson;
if (opt.Replay is not null)
{
    agentJson = File.ReadAllText(opt.Replay);
}
else
{
    var scenarios = Environment.GetEnvironmentVariable("SCENARIOS_PATH")
        ?? Path.Combine(Path.GetDirectoryName(repo)!, "comp-ledger-scenarios");
    var claudeArgs = new List<string>
    {
        "-p", $"/implement-prp {opt.Prp} {runId}",
        "--output-format", "json",
        "--permission-mode", "acceptEdits",
        // Belt and braces: headless runs are already denied reads outside the worktree.
        "--disallowedTools", $"Read({Paths.AbsoluteRule(scenarios)}/**)",
    };
    if (opt.Model is not null) claudeArgs.AddRange(["--model", opt.Model]);
    if (opt.MaxTurns is not null) claudeArgs.AddRange(["--max-turns", opt.MaxTurns]);
    if (opt.MaxBudgetUsd is not null) claudeArgs.AddRange(["--max-budget-usd", opt.MaxBudgetUsd]);

    Console.WriteLine("agent running (this can take a long time)...");
    var claude = Proc.Run(Environment.GetEnvironmentVariable("CLAUDE_BIN") ?? "claude", claudeArgs, worktree);
    File.WriteAllText(Path.Combine(runDir, "claude-stderr.log"), claude.Stderr);
    agentJson = claude.Stdout;
}
File.WriteAllText(Path.Combine(runDir, "agent-result.json"), agentJson);
var finishedAt = DateTimeOffset.UtcNow;

Console.WriteLine("running gates in the worktree...");
var verify = Proc.Run(Paths.Bash(), ["tools/verify.sh"], worktree);
File.WriteAllText(Path.Combine(runDir, "verify.log"), verify.Stdout + verify.Stderr);
var gates = Gates.Parse(verify.Stdout);

var headCommit = Proc.Git(worktree, "rev-parse", "HEAD").Trim();
var commits = int.Parse(Proc.Git(worktree, "rev-list", "--count", $"{baseCommit}..HEAD").Trim());

var record = new JsonObject
{
    ["runId"] = runId,
    ["prp"] = opt.Prp,
    ["prpVersion"] = version,
    ["prpStatus"] = status,
    ["attempt"] = attempt,
    ["replay"] = opt.Replay is not null,
    ["branch"] = branch,
    ["baseCommit"] = baseCommit,
    ["headCommit"] = headCommit,
    ["commits"] = commits,
    ["startedAt"] = startedAt.ToString("O"),
    ["finishedAt"] = finishedAt.ToString("O"),
    ["model"] = opt.Model,
    ["agent"] = Agent.Summarize(agentJson),
    ["gates"] = gates,
    ["humanInterventions"] = 0,
};

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(runsFile))!);
File.AppendAllText(runsFile, record.ToJsonString() + "\n", new UTF8Encoding(false));

Console.WriteLine(record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
if (opt.Replay is not null)
{
    Proc.Git(repo, "worktree", "remove", "--force", worktree);
    Proc.Git(repo, "branch", "-D", branch);
    Console.WriteLine("replay: worktree and branch removed");
}
else
{
    Console.WriteLine($"""

        worktree: {worktree}
        branch:   {branch}
        review:   git -C "{worktree}" log --format="%h %s" {baseCommit}..HEAD
        PR text:  {Path.Combine(runDir, "pr-description.md")}
        """);
}
return 0;

record Options(string Prp, string? Model, string? MaxTurns, string? MaxBudgetUsd, bool AllowDraft, string? Replay, string? RunsFile)
{
    public static Options? Parse(string[] a)
    {
        if (a.Length == 0 || !Regex.IsMatch(a[0], @"^\d{3}$")) return null;
        string? Value(string name)
        {
            var i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }
        return new Options(a[0], Value("--model"), Value("--max-turns"), Value("--max-budget-usd"),
            a.Contains("--allow-draft"), Value("--replay"), Value("--runs-file"));
    }
}

static class Spec
{
    // Reads a value from the PRP header table: "| Status | Ready |".
    public static string Field(string text, string name) =>
        Regex.Match(text, $@"^\|\s*{name}\s*\|\s*(.+?)\s*\|", RegexOptions.Multiline) is { Success: true } m
            ? m.Groups[1].Value
            : "unknown";
}

static class Runs
{
    public static int CountAttempts(string file, string prp, string version)
    {
        if (!File.Exists(file)) return 0;
        return File.ReadLines(file)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonNode.Parse(l)!)
            .Count(n => (string?)n["prp"] == prp && (string?)n["prpVersion"] == version && n["replay"]?.GetValue<bool>() != true);
    }
}

static class Agent
{
    // Keeps the fields that matter for metrics. The raw result stays in the worktree's .prp-run/ folder,
    // because its free-text "result" field doesn't belong in a public log.
    public static JsonObject Summarize(string json)
    {
        JsonNode? r;
        try { r = JsonNode.Parse(json); }
        catch (JsonException) { return new JsonObject { ["parsed"] = false }; }
        if (r is not JsonObject o) return new JsonObject { ["parsed"] = false };

        return new JsonObject
        {
            ["parsed"] = true,
            ["isError"] = o["is_error"]?.DeepClone(),
            ["subtype"] = o["subtype"]?.DeepClone(),
            ["numTurns"] = o["num_turns"]?.DeepClone(),
            ["durationMs"] = o["duration_ms"]?.DeepClone(),
            ["durationApiMs"] = o["duration_api_ms"]?.DeepClone(),
            ["costUsd"] = o["total_cost_usd"]?.DeepClone(),
            ["usage"] = o["usage"]?.DeepClone(),
            ["modelUsage"] = o["modelUsage"]?.DeepClone(),
            ["sessionId"] = o["session_id"]?.DeepClone(),
            ["permissionDenials"] = o["permission_denials"] is JsonArray d ? d.Count : null,
        };
    }
}

static class Gates
{
    // tools/verify.sh prints the gate results as JSON on its last line.
    public static JsonNode Parse(string stdout)
    {
        var last = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        try { return JsonNode.Parse(last ?? "") ?? new JsonObject { ["parsed"] = false }; }
        catch (JsonException) { return new JsonObject { ["parsed"] = false }; }
    }
}

static class Paths
{
    public static string Bash()
    {
        var env = Environment.GetEnvironmentVariable("BASH_PATH");
        if (env is not null) return env;
        if (OperatingSystem.IsWindows())
        {
            var git = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
            if (File.Exists(git)) return git;
        }
        return "bash";
    }

    // Claude Code permission rules use "//" for absolute paths, with forward slashes.
    public static string AbsoluteRule(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        if (OperatingSystem.IsWindows() && full.Length > 1 && full[1] == ':')
            full = "/" + char.ToLowerInvariant(full[0]) + full[2..];
        return "/" + full;
    }
}

static class Proc
{
    public record Result(int ExitCode, string Stdout, string Stderr);

    public static Result Run(string exe, IEnumerable<string> arguments, string cwd)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return new Result(p.ExitCode, stdout.Result, stderr.Result);
    }

    public static string Git(string cwd, params string[] arguments)
    {
        var r = Run("git", arguments, cwd);
        if (r.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {r.Stderr}");
        return r.Stdout;
    }
}
