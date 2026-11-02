using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace CS.AgenticUrlShortner.Orchestrator
{
    internal static class Program
    {
        private const int MaxTestAttempts = 3;

        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || string.IsNullOrWhiteSpace(string.Join(" ", args)))
            {
                Console.Error.WriteLine("Usage: dotnet run --project <orchestrator.csproj> -- \"task\"");
                return 1;
            }

            var repositoryRoot = Directory.GetCurrentDirectory();
            var solution = Path.Combine(repositoryRoot, "AgentUrlShortner", "Backend", "CS.AgenticUrlShortner.sln");
            if (!File.Exists(solution))
            {
                Console.Error.WriteLine("Run this command from the repository root.");
                return 1;
            }

            var task = string.Join(" ", args);
            var prompt = $"Implement this task: {task}\nFollow AGENTS.md. Make minimal changes, run relevant tests, fix failures, and summarize the work. Do not commit or merge.";

            for (var attempt = 1; attempt <= MaxTestAttempts; attempt++)
            {
                var agentExitCode = await RunCopilotAsync(repositoryRoot, prompt).ConfigureAwait(false);
                if (agentExitCode != 0)
                    return agentExitCode;

                var testResult = await RunTestsAsync(repositoryRoot, solution).ConfigureAwait(false);
                if (testResult.ExitCode == 0)
                    return RequestHumanApproval();

                if (attempt == MaxTestAttempts)
                {
                    Console.Error.WriteLine($"Tests still fail after {MaxTestAttempts} attempts.");
                    return testResult.ExitCode;
                }

                prompt = $"The backend test suite failed. Fix the failures and rerun the tests. Do not commit or merge.\nTest output:\n{Tail(testResult.Output)}";
            }

            return 1;
        }

        private static async Task<int> RunCopilotAsync(string repositoryRoot, string prompt)
        {
            var startInfo = new ProcessStartInfo("copilot")
            {
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--autopilot");
            startInfo.ArgumentList.Add("--max-autopilot-continues");
            startInfo.ArgumentList.Add("10");
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(prompt);

            Process process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Win32Exception exception)
            {
                Console.Error.WriteLine($"Could not start Copilot CLI: {exception.Message}");
                return 127;
            }

            if (process == null)
                throw new InvalidOperationException("Could not start Copilot CLI.");

            using (process)
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                return process.ExitCode;
            }
        }

        private static async Task<(int ExitCode, string Output)> RunTestsAsync(string repositoryRoot, string solution)
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add(solution);

            using var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Could not start dotnet test.");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            var combinedOutput = output + error;

            Console.Write(combinedOutput);
            return (process.ExitCode, combinedOutput);
        }

        private static int RequestHumanApproval()
        {
            Console.WriteLine("Tests passed. Review the diff before merging.");
            Console.Write("Type APPROVE after your review, or press Enter to stop: ");
            return string.Equals(Console.ReadLine(), "APPROVE", StringComparison.Ordinal)
                ? 0
                : 2;
        }

        private static string Tail(string output)
        {
            const int MaxOutputLength = 12000;
            return output.Length <= MaxOutputLength ? output : output[^MaxOutputLength..];
        }
    }
}
