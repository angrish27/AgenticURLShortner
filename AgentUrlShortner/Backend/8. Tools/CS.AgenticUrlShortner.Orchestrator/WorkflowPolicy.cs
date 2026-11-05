using System;
using System.Linq;

namespace CS.AgenticUrlShortner.Orchestrator
{
    internal static class WorkflowPolicy
    {
        private static readonly string[] ProhibitedTaskIndicators =
        [
            "exploit", "weaponize", "malware", "ransomware", "credential theft",
            "steal credentials", "exfiltrat", "evade detection", "botnet",
            "phishing kit", "keylogger", "unauthorized access", "attack a target",
            "exploit a target", "scan an external target", "third-party target"
        ];

        public static void Validate(string task)
        {
            if (string.IsNullOrWhiteSpace(task))
                throw new ArgumentException("A non-empty engineering task is required.", nameof(task));
            if (task.Length > 12000)
                throw new ArgumentException("Engineering task exceeds the 12,000-character policy limit.", nameof(task));

            var indicator = ProhibitedTaskIndicators.FirstOrDefault(value =>
                task.Contains(value, StringComparison.OrdinalIgnoreCase));
            if (indicator != null)
                throw new InvalidOperationException(
                    $"Task blocked by the local safety policy (matched '{indicator}'). " +
                    "This runner is limited to defensive engineering changes in the current repository.");
        }
    }
}
