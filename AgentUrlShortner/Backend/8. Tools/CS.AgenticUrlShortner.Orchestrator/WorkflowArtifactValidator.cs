using System;
using System.Linq;

namespace CS.AgenticUrlShortner.Orchestrator
{
    internal static class WorkflowArtifactValidator
    {
        public static string[] MissingSections(StageDefinition definition, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return definition.RequiredArtifactSections;

            return definition.RequiredArtifactSections
                .Where(section => !content.Contains($"## {section}", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }
}
