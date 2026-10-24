using System.Text.Json;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Cli;

/// <summary>Звіт <c>openbp run</c>: по блоку на крок (статус, input і output кожного порту), потім підсумок.</summary>
internal static class RunReport
{
    /// <summary>Друкує звіт виконання.</summary>
    /// <param name="writer">Куди друкувати.</param>
    /// <param name="workflow">Виконаний сценарій.</param>
    /// <param name="result">Результат виконання.</param>
    /// <param name="mode">Режим запуску (для позначки закріплених вузлів).</param>
    public static async Task WriteAsync(TextWriter writer, WorkflowDefinition workflow, ExecutionResult result, ExecutionMode mode)
    {
        var nodes = workflow.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        await writer.WriteLineAsync($"Сценарій «{workflow.Name}»").ConfigureAwait(false);
        foreach (var step in result.Steps)
        {
            var node = nodes[step.NodeId];
            var pinned = mode == ExecutionMode.Manual && workflow.PinData.ContainsKey(step.NodeId) ? " · pinData" : "";
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync($"▶ {node.Name} [{node.Type}] — {step.Status}{pinned}").ConfigureAwait(false);
            await WritePortsAsync(writer, "input", step.Input).ConfigureAwait(false);
            await WritePortsAsync(writer, "output", step.Output).ConfigureAwait(false);
            if (step.Error is { } error)
            {
                await writer.WriteLineAsync($"  помилка: {error.Message}").ConfigureAwait(false);
                if (error.Details is { } details)
                {
                    await writer.WriteLineAsync($"  подробиці: {details}").ConfigureAwait(false);
                }
            }
        }

        await writer.WriteLineAsync().ConfigureAwait(false);
        var summary = $"Результат: {result.Status} за {(result.FinishedAt - result.StartedAt).TotalMilliseconds:0} мс";
        if (result.FailedNodeId is { } failed && nodes.TryGetValue(failed, out var failedNode))
        {
            summary += $"; зупинено на «{failedNode.Name}»";
        }

        if (result.Error is { } resultError)
        {
            summary += $": {resultError.Message}";
        }

        await writer.WriteLineAsync(summary).ConfigureAwait(false);
    }

    private static async Task WritePortsAsync(TextWriter writer, string label, Item[][] ports)
    {
        for (var i = 0; i < ports.Length; i++)
        {
            var json = JsonSerializer.Serialize(ports[i], WorkflowJson.Options);
            await writer.WriteLineAsync($"  {label}[{i}]: {Indent(json)}").ConfigureAwait(false);
        }
    }

    private static string Indent(string text) => text.ReplaceLineEndings(Environment.NewLine + "  ");
}
