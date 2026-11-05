using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CS.AgenticUrlShortner.Orchestrator
{
    internal sealed class WorkflowStore
    {
        private readonly string _runtimeRoot;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public WorkflowStore(string repositoryRoot)
        {
            _runtimeRoot = Path.Combine(repositoryRoot, "AgentUrlShortner", "orchestration", ".runtime");
        }

        public string RuntimeRoot => _runtimeRoot;

        public string RunDirectory(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)
                || runId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                throw new ArgumentException("Run id contains invalid characters.", nameof(runId));
            return Path.Combine(_runtimeRoot, "runs", runId);
        }

        public async Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken)
        {
            var path = Path.Combine(RunDirectory(run.RunId), "state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = path + ".tmp";
            var json = JsonSerializer.Serialize(run, _jsonOptions);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, true);
        }

        public async Task<WorkflowRun> LoadAsync(string runId, CancellationToken cancellationToken)
        {
            var path = Path.Combine(RunDirectory(runId), "state.json");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Workflow run '{runId}' does not exist.", path);

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<WorkflowRun>(json, _jsonOptions)
                ?? throw new InvalidDataException($"Workflow state for run '{runId}' is empty or invalid.");
        }

        public async Task AppendAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            var path = Path.Combine(RunDirectory(auditEvent.RunId), "audit.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(auditEvent, _jsonOptions);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteArtifactAsync(
            WorkflowRun run, string fileName, string content, CancellationToken cancellationToken)
        {
            var path = Path.Combine(RunDirectory(run.RunId), "artifacts", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        }
    }

    internal sealed class RunLock : IDisposable
    {
        private readonly FileStream _stream;

        private RunLock(FileStream stream)
        {
            _stream = stream;
        }

        public static RunLock Acquire(WorkflowStore store, string runId)
        {
            var directory = store.RunDirectory(runId);
            Directory.CreateDirectory(directory);
            try
            {
                var stream = new FileStream(
                    Path.Combine(directory, "run.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return new RunLock(stream);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException(
                    $"Could not acquire the workflow workspace lock; another run may be active: {exception.Message}",
                    exception);
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}
