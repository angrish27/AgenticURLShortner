using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CS.AgenticUrlShortner.ReleaseReadiness
{
    internal enum GateStatus
    {
        Pass,
        Fail,
        Pending
    }

    internal sealed record GateResult(string Name, GateStatus Status, string Evidence);

    internal static class Program
    {
        private static readonly string[] RequiredDocs =
        [
            "scope.md", "decisions.md", "security.md", "testing.md", "architecture.md",
            "orchestration.md", "scenarios/greenfield.md", "scenarios/brownfield.md",
            "scenarios/ambiguous.md"
        ];

        private static readonly Regex SecretPattern = new(
            "\"[^\"]*(password|secret|apikey|api_key|connectionstring|token)[^\"]*\"\\s*:\\s*\"[^\"]+\"",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TestSummaryPattern = new(
            "(Passed|Failed)!\\s+-\\s+Failed:\\s+(\\d+),\\s+Passed:\\s+(\\d+)",
            RegexOptions.Compiled);

        private static int Main(string[] args)
        {
            var options = ParseArgs(args);
            var backendRoot = Path.GetFullPath(options.GetValueOrDefault("root", Directory.GetCurrentDirectory()));
            var docsRoot = Path.GetFullPath(Path.Combine(backendRoot, "..", "docs"));
            var solution = Path.Combine(backendRoot, "CS.AgenticUrlShortner.sln");
            var version = options.GetValueOrDefault("version", "0.1.0");
            var output = options.GetValueOrDefault("output", Path.Combine(docsRoot, "release", "release-readiness.md"));

            if (!File.Exists(solution))
            {
                Console.Error.WriteLine($"Solution not found: {solution}");
                return 1;
            }

            var started = DateTimeOffset.UtcNow;
            var gates = new List<GateResult>
            {
                BuildGate(solution),
                TestGate(solution),
                VulnerabilityGate(solution),
                SecretsGate(backendRoot),
                ConfigGate(backendRoot),
                DocsGate(docsRoot),
                VersionGate(version, options.GetValueOrDefault("previous-version")),
                RollbackGate(options.GetValueOrDefault("previous-version")),
                ApprovalGate(options.GetValueOrDefault("approver"))
            };

            var report = BuildReport(version, started, DateTimeOffset.UtcNow, gates);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, report);

            foreach (var gate in gates)
                Console.WriteLine($"[{gate.Status,-7}] {gate.Name}: {gate.Evidence}");
            Console.WriteLine($"Report: {output}");

            if (gates.Any(g => g.Status == GateStatus.Fail))
                return 1;
            return gates.Any(g => g.Status == GateStatus.Pending) ? 2 : 0;
        }

        private static GateResult BuildGate(string solution)
        {
            var (exitCode, text) = Run("dotnet", $"build \"{solution}\" -c Release -warnaserror -nologo");
            var warnings = Regex.Match(text, "(\\d+) Warning\\(s\\)").Groups[1].Value;
            var errors = Regex.Match(text, "(\\d+) Error\\(s\\)").Groups[1].Value;
            return new GateResult("Build", exitCode == 0 ? GateStatus.Pass : GateStatus.Fail,
                $"dotnet build -c Release -warnaserror: exit {exitCode}, warnings {Blank(warnings)}, errors {Blank(errors)}");
        }

        private static GateResult TestGate(string solution)
        {
            var (exitCode, text) = Run("dotnet", $"test \"{solution}\" -c Release --no-build --nologo");
            var matches = TestSummaryPattern.Matches(text);
            var failed = matches.Sum(m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            var passed = matches.Sum(m => int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            var ok = exitCode == 0 && failed == 0 && passed > 0;
            return new GateResult("Tests", ok ? GateStatus.Pass : GateStatus.Fail,
                $"{matches.Count} test assemblies, {passed} passed, {failed} failed");
        }

        private static GateResult VulnerabilityGate(string solution)
        {
            var (exitCode, text) = Run("dotnet", $"list \"{solution}\" package --vulnerable --include-transitive");
            if (exitCode != 0)
                return new GateResult("Vulnerable packages", GateStatus.Fail, $"dotnet list package failed (exit {exitCode})");

            var vulnerable = text.Contains("has the following vulnerable packages", StringComparison.OrdinalIgnoreCase);
            return new GateResult("Vulnerable packages", vulnerable ? GateStatus.Fail : GateStatus.Pass,
                vulnerable ? "Vulnerable packages reported by dotnet list package --vulnerable" : "No vulnerable packages (including transitive)");
        }

        private static GateResult SecretsGate(string backendRoot)
        {
            var findings = Directory.EnumerateFiles(backendRoot, "appsettings*.json", SearchOption.AllDirectories)
                .Where(f => !IsBuildOutput(f))
                .SelectMany(f => SecretPattern.Matches(File.ReadAllText(f))
                    .Select(m => $"{Path.GetRelativePath(backendRoot, f)}: {m.Value.Split(':')[0]}"))
                .ToList();

            return new GateResult("Secrets in config", findings.Count == 0 ? GateStatus.Pass : GateStatus.Fail,
                findings.Count == 0 ? "No secret-like keys with values in appsettings*.json" : string.Join("; ", findings));
        }

        private static GateResult ConfigGate(string backendRoot)
        {
            var path = Path.Combine(backendRoot, "1. Clients", "CS.AgenticUrlShortner.WebApiClient", "appsettings.json");
            if (!File.Exists(path))
                return new GateResult("Configuration", GateStatus.Fail, "appsettings.json not found");

            var text = File.ReadAllText(path);
            var missing = new[] { "\"UrlStore\"", "\"FilePath\"", "\"RateLimiting\"", "\"EncodePermitLimitPerMinute\"" }
                .Where(k => !text.Contains(k, StringComparison.Ordinal))
                .ToList();

            return new GateResult("Configuration", missing.Count == 0 ? GateStatus.Pass : GateStatus.Fail,
                missing.Count == 0 ? "UrlStore:FilePath and RateLimiting:EncodePermitLimitPerMinute present" : $"Missing: {string.Join(", ", missing)}");
        }

        private static GateResult DocsGate(string docsRoot)
        {
            var missing = RequiredDocs.Where(d => !File.Exists(Path.Combine(docsRoot, d))).ToList();
            return new GateResult("Documentation", missing.Count == 0 ? GateStatus.Pass : GateStatus.Fail,
                missing.Count == 0 ? $"All {RequiredDocs.Length} required docs present" : $"Missing: {string.Join(", ", missing)}");
        }

        private static GateResult VersionGate(string version, string previousVersion)
        {
            if (!Version.TryParse(version, out var current))
                return new GateResult("Version", GateStatus.Fail, $"'{version}' is not a valid version");

            if (previousVersion != null && Version.TryParse(previousVersion, out var previous) && current <= previous)
                return new GateResult("Version", GateStatus.Fail, $"{version} is not greater than previous {previousVersion}");

            return new GateResult("Version", GateStatus.Pass, $"Release version {version}");
        }

        private static GateResult RollbackGate(string previousVersion)
        {
            return previousVersion == null
                ? new GateResult("Rollback target", GateStatus.Pass, "First release: rollback = stop the service; the store file is the only state (back it up before release)")
                : new GateResult("Rollback target", GateStatus.Pass, $"Redeploy {previousVersion}; restore the url-mappings.json backup taken before release");
        }

        private static GateResult ApprovalGate(string approver)
        {
            return string.IsNullOrWhiteSpace(approver)
                ? new GateResult("Human approval", GateStatus.Pending, "Not approved. Re-run with --approver \"<name>\" after review")
                : new GateResult("Human approval", GateStatus.Pass, $"Approved by {approver} at {DateTimeOffset.UtcNow:u}");
        }

        private static string BuildReport(string version, DateTimeOffset started, DateTimeOffset finished, List<GateResult> gates)
        {
            var verdict = gates.Any(g => g.Status == GateStatus.Fail) ? "NOT READY"
                : gates.Any(g => g.Status == GateStatus.Pending) ? "READY PENDING APPROVAL"
                : "READY";

            var sb = new StringBuilder();
            sb.AppendLine("# Release Readiness Report");
            sb.AppendLine();
            sb.AppendLine("> Generated by `8. Tools/CS.AgenticUrlShortner.ReleaseReadiness`. Do not edit by hand; re-run the tool.");
            sb.AppendLine();
            sb.AppendLine($"- **Version:** {version}");
            sb.AppendLine($"- **Generated (UTC):** {finished:u}");
            sb.AppendLine($"- **Duration:** {(finished - started).TotalSeconds:F1}s");
            sb.AppendLine($"- **Machine:** {Environment.MachineName}");
            sb.AppendLine($"- **Verdict:** **{verdict}**");
            sb.AppendLine();
            sb.AppendLine("| Gate | Status | Evidence |");
            sb.AppendLine("|---|---|---|");
            foreach (var gate in gates)
                sb.AppendLine($"| {gate.Name} | {Icon(gate.Status)} {gate.Status} | {gate.Evidence.Replace("|", "\\|", StringComparison.Ordinal)} |");
            sb.AppendLine();
            sb.AppendLine("## Rollback procedure");
            sb.AppendLine("1. Stop the API.");
            sb.AppendLine("2. Restore the `App_Data/url-mappings.json` backup taken before release (never edit it by hand; a corrupt file blocks startup by design).");
            sb.AppendLine("3. Redeploy the previous build and verify `GET /health` returns 200.");
            sb.AppendLine();
            sb.AppendLine("## Exit codes");
            sb.AppendLine("`0` ready, `1` one or more gates failed, `2` all gates passed but human approval is pending.");
            return sb.ToString();
        }

        private static (int ExitCode, string Output) Run(string fileName, string arguments)
        {
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(startInfo)!;
            var stdErr = process.StandardError.ReadToEndAsync();
            var stdOut = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdOut + stdErr.Result);
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                    result[args[i][2..]] = args[++i];
            }

            return result;
        }

        private static bool IsBuildOutput(string path) =>
            path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

        private static string Blank(string value) => string.IsNullOrEmpty(value) ? "n/a" : value;

        private static string Icon(GateStatus status) => status switch
        {
            GateStatus.Pass => "✅",
            GateStatus.Fail => "❌",
            _ => "⏳"
        };
    }
}
