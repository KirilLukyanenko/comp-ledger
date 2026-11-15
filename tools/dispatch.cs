#:property PublishAot=false

// Runs several PRPs at the same time, each through tools/run-prp.cs in its own worktree, under one budget.
//
//   dotnet run tools/dispatch.cs -- <prp>... [options]     e.g. 003 004
//   dotnet run tools/dispatch.cs -- --ready [options]      every spec whose status is Ready
//   dotnet run tools/dispatch.cs -- conflicts <branch> <branch>...
//
// Options:
//   --budget-usd <x>       total for the whole dispatch at API prices (required)
//   --parallel <n>         runs at the same time (default 2)
//   --max-budget-usd <x>   cap per run, passed to run-prp.cs (default: budget / parallel)
//   --model <alias|id>     passed to run-prp.cs
//   --allow-draft          passed to run-prp.cs; also lets the plan take specs that are not Ready
//   --replay <file>        passed to run-prp.cs: no agent, a saved result; worktrees are removed afterwards
//   --runs-file <path>     passed to run-prp.cs
//   --dry-run              print the plan and stop
//
// What it does, in order:
// 1. Plan. A spec is skipped, with the reason, if it is not Ready, if a spec it depends on is not Implemented
//    (a dependency in the same batch counts as not implemented: its branch must be merged first), if a run of it
//    is already in progress, or if a run of its current version passed the gates and its branch waits for review.
// 2. Queue. At most --parallel runs at once. A run starts only if the money already spent, plus the cap of every
//    running run, plus its own cap fits in --budget-usd, so the total can't be exceeded even if every run uses
//    its whole cap. Runs start one after another: the next one waits until the previous one has its worktree,
//    because two `git worktree add` at the same moment fight over the repository's locks.
// 3. Conflicts. When the runs are done, every pair of branches with commits is merged in memory
//    (`git merge-tree`, nothing is written to a branch): which files both changed, and whether they conflict.
//    That decides the merge order, which stays a human decision.
//
// State: .prp-run/dispatches/<id>.json (gitignored), rewritten on every change, so Tower and a second terminal
// can watch the queue. Each run's output goes next to it. A finished real dispatch appends one line to
// metrics/dispatches.jsonl. Ctrl+C stops every run; their worktrees are kept, as run-prp.cs keeps them.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var repo = Git.Run(Environment.CurrentDirectory, "rev-parse", "--show-toplevel").Trim();

if (args.Length > 0 && args[0] == "conflicts")
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: dotnet run tools/dispatch.cs -- conflicts <branch> <branch>...");
        return 2;
    }
    var report = Conflicts.Check(repo, args.Skip(1).Select(b => (Label: b, Branch: b)).ToList());
    Conflicts.Print(report);
    return report.Any(c => !c.Clean) ? 1 : 0;
}

var opt = Options.Parse(args);
if (opt is null)
{
    Console.Error.WriteLine("usage: dotnet run tools/dispatch.cs -- (<prp>... | --ready) --budget-usd <x> [--parallel n] [--max-budget-usd x] [--model m] [--allow-draft] [--replay file] [--runs-file path] [--dry-run]");
    return 2;
}
if (opt.Replay is null && !opt.DryRun && Git.Run(repo, "status", "--porcelain", "--", "specs").Trim().Length > 0)
{
    Console.Error.WriteLine("specs/ has uncommitted changes. Commit them first: the agents only see committed specs.");
    return 2;
}

var runsFiles = new[] { Path.Combine(repo, "metrics", "runs.jsonl"), opt.RunsFile }.OfType<string>().Distinct().ToList();
var plan = Plan.Make(repo, opt, runsFiles);
var perRun = opt.MaxBudgetUsd ?? Math.Round(opt.BudgetUsd / opt.Parallel, 2);

Console.WriteLine($"plan: {plan.Count(i => i.State == "queued")} to run, {opt.Parallel} at a time, budget ${opt.BudgetUsd:0.##}, cap per run ${perRun:0.##}");
foreach (var i in plan)
    Console.WriteLine($"  PRP-{i.Prp} {i.Version,-5} {(i.State == "queued" ? "queued" : "skipped: " + i.Reason)}");
if (opt.DryRun) return 0;
if (plan.All(i => i.State != "queued")) return 1;

var dispatch = new Dispatch(repo, opt, perRun, plan);
dispatch.Save();
Console.WriteLine($"dispatch {dispatch.Id}: state in {Path.GetRelativePath(repo, dispatch.StateFile)}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (cts.IsCancellationRequested) return;
    Console.WriteLine("stopping every run...");
    cts.Cancel();
};

await dispatch.Run(cts.Token);
return dispatch.Items.All(i => i.State is "passed" or "skipped") ? 0 : 1;

record Options(List<string> Prps, bool Ready, int Parallel, decimal BudgetUsd, decimal? MaxBudgetUsd, string? Model,
    bool AllowDraft, string? Replay, string? RunsFile, bool DryRun)
{
    public static Options? Parse(string[] a)
    {
        string? Value(string name)
        {
            var i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }
        decimal? Money(string name) => decimal.TryParse(Value(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

        var prps = a.TakeWhile(x => !x.StartsWith("--")).ToList();
        if (prps.Any(p => !Regex.IsMatch(p, @"^\d{3}$"))) return null;
        var ready = a.Contains("--ready");
        if (prps.Count == 0 == !ready) return null;
        var budget = Money("--budget-usd");
        var dryRun = a.Contains("--dry-run");
        if (budget is null && !dryRun) return null;
        var parallel = int.TryParse(Value("--parallel"), out var n) && n > 0 ? n : 2;
        return new Options(prps.Distinct().ToList(), ready, parallel, budget ?? 0, Money("--max-budget-usd"), Value("--model"),
            a.Contains("--allow-draft"), Value("--replay"), Value("--runs-file"), dryRun);
    }
}

/// <summary>One spec in the dispatch. States: queued, skipped, running, passed, failed, stopped.</summary>
sealed class Item
{
    public required string Prp { get; init; }
    public required string Version { get; init; }
    public string State { get; set; } = "queued";
    public string? Reason { get; set; }
    public string? Stage { get; set; }          // starting, agent, gates
    public string? RunId { get; set; }
    public string? Branch { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public decimal? CostUsd { get; set; }
    public int? Commits { get; set; }
    public JsonNode? Gates { get; set; }
    public string? Log { get; set; }

    public JsonObject ToJson() => new()
    {
        ["prp"] = Prp,
        ["version"] = Version,
        ["state"] = State,
        ["reason"] = Reason,
        ["stage"] = Stage,
        ["runId"] = RunId,
        ["branch"] = Branch,
        ["startedAt"] = StartedAt?.ToString("O"),
        ["finishedAt"] = FinishedAt?.ToString("O"),
        ["costUsd"] = CostUsd,
        ["commits"] = Commits,
        ["gates"] = Gates?.DeepClone(),
        ["log"] = Log,
    };
}

static class Plan
{
    // Worktrees younger than this, with no record and no gate log, are runs in progress. Older ones were abandoned.
    static readonly TimeSpan InProgressFor = TimeSpan.FromHours(12);

    public static List<Item> Make(string repo, Options opt, List<string> runsFiles)
    {
        var specs = Directory.GetFiles(Path.Combine(repo, "specs"), "*.prp.md")
            .Select(Path.GetFileName).OfType<string>()
            .Where(f => Regex.IsMatch(f, @"^\d{3}-"))
            .ToDictionary(f => f[..3], f => Git.Run(repo, "show", $"HEAD:specs/{f}"));
        var numbers = opt.Ready
            ? specs.Where(s => Spec.Field(s.Value, "Status") == "Ready").Select(s => s.Key).Order().ToList()
            : opt.Prps;
        var batch = numbers.ToHashSet();

        var records = runsFiles.Where(File.Exists)
            .SelectMany(f => File.ReadLines(f).Where(l => l.Trim().Length > 0).Select(l => JsonNode.Parse(l)!))
            .ToList();
        var recorded = records.Select(r => (string?)r["runId"]).OfType<string>().ToHashSet();
        var unmerged = Git.Run(repo, "branch", "--no-merged", "main", "--format=%(refname:short)", "--list", "agent/*")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var inProgress = InProgress(repo, recorded);

        var items = new List<Item>();
        foreach (var prp in numbers)
        {
            if (!specs.TryGetValue(prp, out var text))
            {
                items.Add(new Item { Prp = prp, Version = "?", State = "skipped", Reason = $"no spec matching specs/{prp}-*.prp.md" });
                continue;
            }
            var item = new Item { Prp = prp, Version = Spec.Field(text, "Version") };
            var status = Spec.Field(text, "Status");
            var deps = Regex.Matches(Spec.Field(text, "Depends on"), @"PRP-(\d{3})").Select(m => m.Groups[1].Value)
                .Where(d => d != prp).Distinct().ToList();
            var greenWaiting = records.FirstOrDefault(r => (string?)r["prp"] == prp && (string?)r["prpVersion"] == item.Version
                && r["replay"]?.GetValue<bool>() != true && Gates.AllPass(r["gates"]) && unmerged.Contains((string?)r["branch"] ?? ""));

            // A dependency must be merged: an Implemented spec. One that runs in this batch isn't merged yet.
            var missing = deps.FirstOrDefault(d => !specs.ContainsKey(d) || Spec.Field(specs[d], "Status") != "Implemented");
            item.Reason =
                status != "Ready" && !opt.AllowDraft ? $"status is {status}, not Ready" :
                missing is not null && batch.Contains(missing) ? $"depends on PRP-{missing} from this batch: merge that first" :
                missing is not null ? $"depends on PRP-{missing}, which is {(specs.TryGetValue(missing, out var t) ? Spec.Field(t, "Status") : "missing")}" :
                inProgress.FirstOrDefault(r => r.Prp == prp) is { RunId: { } running } ? $"run {running} is already in progress" :
                greenWaiting is not null ? $"run {greenWaiting["runId"]} passed the gates; its branch waits for review" :
                null;
            if (item.Reason is not null) item.State = "skipped";
            items.Add(item);
        }
        return items;
    }

    /// <summary>Runs started by run-prp.cs, here or elsewhere, that have not written their record yet.</summary>
    public static List<(string Prp, string RunId)> InProgress(string repo, HashSet<string> recorded)
    {
        var dir = Path.Combine(Path.GetDirectoryName(repo)!, "comp-ledger-worktrees");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetDirectories(dir)
            .Select(d => (Dir: d, Name: Path.GetFileName(d)))
            .Where(w => Regex.IsMatch(w.Name, @"^prp\d{3}-") && !recorded.Contains(w.Name)
                && !File.Exists(Path.Combine(w.Dir, ".prp-run", "verify.log"))
                && DateTime.UtcNow - Directory.GetCreationTimeUtc(w.Dir) < InProgressFor)
            .Select(w => (w.Name[3..6], w.Name))
            .ToList();
    }
}

sealed class Dispatch
{
    readonly string repo;
    readonly Options opt;
    readonly decimal perRun;
    readonly object gate = new();
    readonly DateTime startedAt = DateTime.UtcNow;
    readonly List<Process> processes = [];
    List<Conflicts.Pair> conflicts = [];
    string state = "running";
    DateTime? finishedAt;

    public string Id { get; } = $"d{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid():N}"[..23];
    public List<Item> Items { get; }
    public string StateFile { get; }
    string LogDir => Path.Combine(Path.GetDirectoryName(StateFile)!, Id);

    public Dispatch(string repo, Options opt, decimal perRun, List<Item> items)
    {
        this.repo = repo;
        this.opt = opt;
        this.perRun = perRun;
        Items = items;
        StateFile = Path.Combine(repo, ".prp-run", "dispatches", Id + ".json");
        Directory.CreateDirectory(LogDir);
    }

    decimal Spent => Items.Sum(i => i.CostUsd ?? 0);

    public async Task Run(CancellationToken stop)
    {
        var queue = new Queue<Item>(Items.Where(i => i.State == "queued"));
        var running = new Dictionary<Task, Item>();

        while (queue.Count > 0 || running.Count > 0)
        {
            while (queue.Count > 0 && running.Count < opt.Parallel && !stop.IsCancellationRequested)
            {
                // Reserve the full cap of every running run: the budget holds even if each one spends all of it.
                if (Spent + perRun * (running.Count + 1) > opt.BudgetUsd) break;
                var item = queue.Dequeue();
                var worktreeReady = new TaskCompletionSource();
                running[RunOne(item, worktreeReady, stop)] = item;
                await worktreeReady.Task;
            }

            if (running.Count == 0)
            {
                foreach (var left in queue)
                {
                    left.State = stop.IsCancellationRequested ? "stopped" : "skipped";
                    left.Reason = stop.IsCancellationRequested ? "dispatch stopped" : $"budget: ${Spent:0.##} spent, a run may take ${perRun:0.##}, total ${opt.BudgetUsd:0.##}";
                    Say(left, left.Reason);
                }
                break;
            }

            var done = await Task.WhenAny(running.Keys.Append(Task.Delay(Timeout.Infinite, stop)));
            if (stop.IsCancellationRequested)
            {
                lock (gate) foreach (var p in processes) Try(() => p.Kill(entireProcessTree: true));
                await Task.WhenAll(running.Keys);
                running.Clear();
                continue;
            }
            running.Remove(done);
        }

        conflicts = Conflicts.Check(repo, Items.Where(i => i.Commits > 0 && i.Branch is not null && Git.BranchExists(repo, i.Branch))
            .Select(i => (Label: $"PRP-{i.Prp}", Branch: i.Branch!)).ToList());
        state = stop.IsCancellationRequested ? "stopped" : "done";
        finishedAt = DateTime.UtcNow;
        Save();
        if (opt.Replay is null) Record();
        Summary();
    }

    async Task RunOne(Item item, TaskCompletionSource worktreeReady, CancellationToken stop)
    {
        try
        {
            await RunProcess(item, worktreeReady, stop);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or JsonException)
        {
            (item.State, item.Reason, item.Stage, item.FinishedAt) = ("failed", e.Message, null, DateTime.UtcNow);
            Save();
            Say(item, "failed: " + e.Message);
        }
        finally
        {
            worktreeReady.TrySetResult();
        }
    }

    async Task RunProcess(Item item, TaskCompletionSource worktreeReady, CancellationToken stop)
    {
        item.State = "running";
        item.Stage = "starting";
        item.StartedAt = DateTime.UtcNow;
        item.Log = Path.GetRelativePath(repo, Path.Combine(LogDir, $"prp{item.Prp}.log")).Replace('\\', '/');
        Save();
        Say(item, "starting");

        var runArgs = new List<string> { "run", "tools/run-prp.cs", "--", item.Prp, "--max-budget-usd", perRun.ToString(CultureInfo.InvariantCulture) };
        if (opt.Model is not null) runArgs.AddRange(["--model", opt.Model]);
        if (opt.AllowDraft) runArgs.Add("--allow-draft");
        if (opt.Replay is not null) runArgs.AddRange(["--replay", Path.GetFullPath(opt.Replay)]);
        if (opt.RunsFile is not null) runArgs.AddRange(["--runs-file", Path.GetFullPath(opt.RunsFile)]);

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in runArgs) psi.ArgumentList.Add(a);

        using var log = new StreamWriter(Path.Combine(repo, item.Log), append: false, new UTF8Encoding(false)) { AutoFlush = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start dotnet");
        lock (gate) processes.Add(p);

        var lastError = "";
        var stderr = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync() is { } line)
            {
                lock (log) log.WriteLine("! " + line);
                if (line.Trim().Length > 0) lastError = line.Trim();
            }
        });
        while (await p.StandardOutput.ReadLineAsync() is { } line)
        {
            lock (log) log.WriteLine(line);
            if (Regex.Match(line, @"^run (prp\d{3}-\S+): ") is { Success: true } m)
            {
                item.RunId = m.Groups[1].Value;
                item.Branch = "agent/" + item.RunId;
                Save();
            }
            else if (line.StartsWith("agent running")) Stage(item, "agent", worktreeReady);
            else if (line.StartsWith("running gates")) Stage(item, "gates", worktreeReady);
        }
        await stderr;
        await p.WaitForExitAsync(CancellationToken.None);
        worktreeReady.TrySetResult();

        item.FinishedAt = DateTime.UtcNow;
        item.Stage = null;
        var record = item.RunId is null ? null : RunRecord(item.RunId);
        if (record is not null)
        {
            item.Commits = record["commits"]?.GetValue<int>();
            item.CostUsd = record["agent"]?["costUsd"]?.GetValue<decimal>() ?? 0;
            item.Gates = record["gates"]?.DeepClone();
        }

        if (stop.IsCancellationRequested) (item.State, item.Reason) = ("stopped", "dispatch stopped");
        else if (record is null) (item.State, item.Reason) = ("failed", $"run-prp.cs exited {p.ExitCode} without a record: {lastError}");
        else if (record["agent"]?["isError"]?.GetValue<bool>() == true) (item.State, item.Reason) = ("failed", $"agent stopped: {record["agent"]?["subtype"]}");
        else if (!Gates.AllPass(item.Gates)) (item.State, item.Reason) = ("failed", "gates: " + Gates.Failed(item.Gates));
        else (item.State, item.Reason) = ("passed", null);

        // A replay removes its branch: there is nothing left to merge or compare.
        if (opt.Replay is not null) item.Branch = null;
        Save();
        Say(item, item.State + (item.Reason is null ? "" : $" ({item.Reason})") + $", ${item.CostUsd ?? 0:0.00}");
    }

    void Stage(Item item, string stage, TaskCompletionSource worktreeReady)
    {
        item.Stage = stage;
        worktreeReady.TrySetResult();
        Save();
        Say(item, stage == "agent" ? "agent working" : "gates running");
    }

    JsonNode? RunRecord(string runId) =>
        (opt.RunsFile is not null ? [opt.RunsFile] : new[] { Path.Combine(repo, "metrics", "runs.jsonl") })
            .Where(File.Exists)
            .SelectMany(f => File.ReadLines(f).Where(l => l.Contains(runId)).Select(l => JsonNode.Parse(l)))
            .LastOrDefault(n => (string?)n?["runId"] == runId);

    JsonObject ToJson()
    {
        var finished = Items.Where(i => i.StartedAt is not null && i.FinishedAt is not null).ToList();
        return new JsonObject
        {
            ["dispatchId"] = Id,
            ["state"] = state,
            ["pid"] = Environment.ProcessId,
            ["startedAt"] = startedAt.ToString("O"),
            ["finishedAt"] = finishedAt?.ToString("O"),
            ["parallel"] = opt.Parallel,
            ["budgetUsd"] = opt.BudgetUsd,
            ["perRunBudgetUsd"] = perRun,
            ["spentUsd"] = Spent,
            ["replay"] = opt.Replay is not null,
            ["model"] = opt.Model,
            // Time the runs took one after another, against the time the dispatch took: what running in parallel saved.
            ["sumRunMs"] = (long)finished.Sum(i => (i.FinishedAt!.Value - i.StartedAt!.Value).TotalMilliseconds),
            ["wallMs"] = finishedAt is null ? null : (long)(finishedAt.Value - startedAt).TotalMilliseconds,
            ["items"] = new JsonArray(Items.Select(i => (JsonNode)i.ToJson()).ToArray()),
            ["conflicts"] = new JsonArray(conflicts.Select(c => (JsonNode)c.ToJson()).ToArray()),
        };
    }

    public void Save()
    {
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            var tmp = StateFile + ".tmp";
            File.WriteAllText(tmp, ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(tmp, StateFile, overwrite: true);
        }
    }

    void Record()
    {
        var line = ToJson();
        line.Remove("pid");
        foreach (var i in line["items"]!.AsArray()) i!.AsObject().Remove("log");
        var file = Path.Combine(repo, "metrics", "dispatches.jsonl");
        File.AppendAllText(file, line.ToJsonString() + "\n", new UTF8Encoding(false));
    }

    void Summary()
    {
        Console.WriteLine();
        Console.WriteLine($"dispatch {Id}: {state}");
        foreach (var i in Items)
        {
            var took = i.StartedAt is { } s && i.FinishedAt is { } f ? $"{(f - s).TotalMinutes,5:0.0} min" : "        -";
            Console.WriteLine($"  PRP-{i.Prp}  {i.State,-8} {took}  ${i.CostUsd ?? 0,6:0.00}  {i.RunId ?? ""} {i.Reason ?? ""}");
        }
        var json = ToJson();
        var wall = (long?)json["wallMs"] ?? 0;
        var sum = (long?)json["sumRunMs"] ?? 0;
        Console.WriteLine($"  spent ${Spent:0.00} of ${opt.BudgetUsd:0.##}; runs took {sum / 60000.0:0.0} min one after another, {wall / 60000.0:0.0} min in parallel");
        if (conflicts.Count > 0) Conflicts.Print(conflicts);
        Console.WriteLine($"  state: {Path.GetRelativePath(repo, StateFile)}");
    }

    void Say(Item item, string text) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} PRP-{item.Prp} {text}");

    static void Try(Action a)
    {
        try { a(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
}

static class Conflicts
{
    public sealed record Pair(string A, string B, string BranchA, string BranchB, bool Clean, List<string> Conflicted, List<string> BothChanged)
    {
        public JsonObject ToJson() => new()
        {
            ["a"] = A,
            ["b"] = B,
            ["branchA"] = BranchA,
            ["branchB"] = BranchB,
            ["clean"] = Clean,
            ["conflicted"] = new JsonArray(Conflicted.Select(f => (JsonNode)f).ToArray()),
            ["bothChanged"] = new JsonArray(BothChanged.Select(f => (JsonNode)f).ToArray()),
        };
    }

    /// <summary>Every pair merged in memory. Nothing is written to a branch or the working tree.</summary>
    public static List<Pair> Check(string repo, List<(string Label, string Branch)> branches)
    {
        // Files a branch changed since the point where the pair split: what a merge of the two has to combine.
        HashSet<string> Changed(string from, string branch) =>
            Git.Run(repo, "diff", "--name-only", $"{from}..{branch}").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var pairs = new List<Pair>();
        for (var i = 0; i < branches.Count; i++)
            for (var j = i + 1; j < branches.Count; j++)
            {
                var (a, b) = (branches[i], branches[j]);
                var r = Git.Raw(repo, "merge-tree", "--write-tree", "--name-only", "--no-messages", a.Branch, b.Branch);
                if (r.ExitCode > 1) throw new InvalidOperationException($"git merge-tree failed: {r.Stderr}");
                // Exit 0: clean, the output is the merged tree. Exit 1: conflicts, the tree, then one conflicted path per line.
                var conflicted = r.ExitCode == 0 ? [] : r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).Distinct().ToList();
                var split = Git.Run(repo, "merge-base", a.Branch, b.Branch).Trim();
                var both = Changed(split, a.Branch).Intersect(Changed(split, b.Branch)).Order().ToList();
                pairs.Add(new Pair(a.Label, b.Label, a.Branch, b.Branch, r.ExitCode == 0, conflicted, both));
            }
        return pairs;
    }

    public static void Print(List<Pair> pairs)
    {
        Console.WriteLine("  branches merged in memory:");
        foreach (var p in pairs)
        {
            var verdict = p.Clean
                ? p.BothChanged.Count == 0 ? "independent: merge in any order" : $"merge cleanly, both change {p.BothChanged.Count} file(s): {string.Join(", ", p.BothChanged.Take(5))}"
                : $"CONFLICT in {string.Join(", ", p.Conflicted.Take(5))}: merge one, rerun the other on the new main";
            Console.WriteLine($"    {p.A} + {p.B}: {verdict}");
        }
    }
}

static class Gates
{
    static readonly string[] Names = ["build", "format", "unit", "integration"];

    public static bool AllPass(JsonNode? gates) => gates is JsonObject o && Names.All(n => (string?)o[n] == "pass");

    public static string Failed(JsonNode? gates) =>
        gates is JsonObject o && o["parsed"] is null
            ? string.Join(", ", Names.Where(n => (string?)o[n] != "pass").Select(n => $"{n} {(string?)o[n] ?? "missing"}"))
            : "no result";
}

static class Spec
{
    // Reads a value from the PRP header table: "| Status | Ready |".
    public static string Field(string text, string name) =>
        Regex.Match(text, $@"^\|\s*{Regex.Escape(name)}\s*\|\s*(.+?)\s*\|", RegexOptions.Multiline) is { Success: true } m
            ? m.Groups[1].Value
            : "unknown";
}

static class Git
{
    public record Result(int ExitCode, string Stdout, string Stderr);

    public static Result Raw(string cwd, params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start git");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return new Result(p.ExitCode, stdout.Result, stderr.Result);
    }

    public static string Run(string cwd, params string[] arguments)
    {
        var r = Raw(cwd, arguments);
        if (r.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {r.Stderr}");
        return r.Stdout;
    }

    public static bool BranchExists(string repo, string branch) => Raw(repo, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch).ExitCode == 0;
}
