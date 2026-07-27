using Priest.Schema;

namespace Priest.Engine;

/// <summary>Executes one tool call. Errors should be returned as content with IsError, not thrown.</summary>
public delegate Task<ToolExecutionResult> ToolExecutor(ToolCall call);

/// <summary>Approval gate called before each execution. Returning Approved=false injects a denial result.</summary>
public delegate Task<ApprovalDecision> ToolApprovalHook(ToolCall call);

public record ToolExecutionResult(string Content, bool IsError = false);

public record ApprovalDecision(bool Approved, string? Reason = null);

/// <summary>Result of a RunWithToolsAsync loop.</summary>
public record ToolLoopResult(
    /// <summary>The final response — the first one without tool calls, or the
    /// last iteration's response when the cap was hit or an error occurred.</summary>
    PriestResponse Response,
    /// <summary>Full tool exchange trace accumulated across iterations.</summary>
    IReadOnlyList<ToolExchangeTurn> Exchange,
    /// <summary>True when the loop stopped because the iteration cap was reached.</summary>
    bool IterationLimitReached);

/// <summary>
/// Generic caller-executes tool loop (spec 2.4.0, behavior/tool-calling.md):
/// run the request, execute tool calls through the caller-supplied executor,
/// replay results via ToolExchange, and repeat until the model answers
/// without tool calls or the iteration cap is hit. The library never chooses
/// or sandboxes tools — policy belongs to the caller.
/// </summary>
public static class ToolLoop
{
    private const int DefaultMaxIterations = 10;

    public static async Task<ToolLoopResult> RunWithToolsAsync(
        PriestEngine engine,
        PriestRequest request,
        ToolExecutor executor,
        ToolApprovalHook? onToolCall = null,
        int maxIterations = DefaultMaxIterations,
        CancellationToken ct = default)
    {
        maxIterations = Math.Max(1, maxIterations);
        var exchange = new List<ToolExchangeTurn>(request.ToolExchange);

        PriestResponse? response = null;
        for (var i = 0; i < maxIterations; i++)
        {
            request.ToolExchange = exchange;
            response = await engine.RunAsync(request, ct);
            if (!response.Ok || response.ToolCalls is not { Count: > 0 } calls)
                return new ToolLoopResult(response, exchange, false);

            exchange.Add(new AssistantToolTurn(response.Text, calls, response.Reasoning));
            foreach (var call in calls)
            {
                var decision = onToolCall is not null ? await onToolCall(call) : new ApprovalDecision(true);
                if (!decision.Approved)
                {
                    var reason = decision.Reason is not null ? $": {decision.Reason}" : ".";
                    exchange.Add(new ToolResultTurn(call.Id, call.Name, $"Tool call denied by the caller{reason}", IsError: true));
                    continue;
                }
                var result = await executor(call);
                exchange.Add(new ToolResultTurn(call.Id, call.Name, result.Content, result.IsError));
            }
        }

        return new ToolLoopResult(response!, exchange, true);
    }
}
