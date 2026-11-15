// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

internal sealed class AssistantToolWorkflow
{
    readonly HashSet<string> failedRequests = new(StringComparer.Ordinal);
    readonly HashSet<string> failedOperations = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HashSet<string>> prerequisitesByDependent = new(StringComparer.OrdinalIgnoreCase);

    internal AssistantToolWorkflow(IEnumerable<DependencyRule>? dependencyRules = null)
    {
        foreach (DependencyRule rule in dependencyRules ?? Enumerable.Empty<DependencyRule>())
        {
            if (string.IsNullOrWhiteSpace(rule.PrerequisiteOperation)
                || string.IsNullOrWhiteSpace(rule.DependentOperation))
            {
                continue;
            }

            if (!this.prerequisitesByDependent.TryGetValue(rule.DependentOperation, out HashSet<string>? prerequisites))
            {
                prerequisites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                this.prerequisitesByDependent.Add(rule.DependentOperation, prerequisites);
            }

            prerequisites.Add(rule.PrerequisiteOperation);
        }
    }

    internal async Task<IReadOnlyList<ToolExecution>> ExecuteSequentialAsync(
        IReadOnlyList<ChatToolCall> calls,
        Func<ChatToolCall, Task<string?>> invokeAsync)
    {
        var executions = new List<ToolExecution>(calls.Count);
        bool failedMutationInBatch = false;
        foreach (ChatToolCall call in calls)
        {
            string? result;
            ToolExecutionDisposition disposition;
            if (this.IsDuplicateFailedRequest(call))
            {
                result = DuplicateFailureResult(call.FunctionName);
                disposition = ToolExecutionDisposition.DuplicateBlocked;
            }
            else if (failedMutationInBatch && IsMutation(call.FunctionName))
            {
                result = DependencyFailureResult(call.FunctionName);
                disposition = ToolExecutionDisposition.DependencyBlocked;
            }
            else if (this.HasFailedPrerequisite(call.FunctionName))
            {
                result = DependencyFailureResult(call.FunctionName);
                disposition = ToolExecutionDisposition.DependencyBlocked;
            }
            else
            {
                result = await invokeAsync(call);
                disposition = ToolExecutionDisposition.Invoked;
            }

            this.Record(call, result);
            failedMutationInBatch |= IsMutation(call.FunctionName) && IsFailure(result);
            executions.Add(new ToolExecution(call, result, disposition));
        }

        return executions;
    }

    internal bool IsDuplicateFailedRequest(ChatToolCall call) =>
        this.failedRequests.Contains(Fingerprint(call));

    internal void Record(ChatToolCall call, string? result)
    {
        if (IsFailure(result))
        {
            this.failedRequests.Add(Fingerprint(call));
            this.failedOperations.Add(call.FunctionName);
            return;
        }

        this.failedOperations.Remove(call.FunctionName);
    }

    bool HasFailedPrerequisite(string? functionName) =>
        !string.IsNullOrWhiteSpace(functionName)
        && this.prerequisitesByDependent.TryGetValue(functionName, out HashSet<string>? prerequisites)
        && prerequisites.Overlaps(this.failedOperations);

    internal static bool IsMutation(string? functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
        {
            return false;
        }

        return functionName.StartsWith("Set", StringComparison.OrdinalIgnoreCase)
            || functionName.StartsWith("Add", StringComparison.OrdinalIgnoreCase)
            || functionName.StartsWith("Update", StringComparison.OrdinalIgnoreCase)
            || functionName.StartsWith("Replace", StringComparison.OrdinalIgnoreCase)
            || functionName.StartsWith("Delete", StringComparison.OrdinalIgnoreCase)
            || functionName.StartsWith("Patch", StringComparison.OrdinalIgnoreCase)
            || functionName.StartsWith("Generate", StringComparison.OrdinalIgnoreCase)
            || functionName.StartsWith("Finalize", StringComparison.OrdinalIgnoreCase)
            || functionName.StartsWith("Sync", StringComparison.OrdinalIgnoreCase)
            || functionName.StartsWith("Confirm", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsFailure(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return true;
        }

        JToken? token = ParseResult(result);
        if (token is JObject obj)
        {
            if (obj.TryGetValue("success", StringComparison.OrdinalIgnoreCase, out JToken? success)
                && success.Type == JTokenType.Boolean)
            {
                return !success.Value<bool>();
            }

            string? outcome = obj.GetValue("outcome", StringComparison.OrdinalIgnoreCase)?.Value<string>();
            return outcome is "assistant_retry_required" or "needs_user_input" or "needs_reconciliation"
                or "constraint_violation" or "failed";
        }

        if (token is not JValue { Type: JTokenType.String } scalar)
        {
            // Arrays and other non-object JSON values are valid tool data. In particular,
            // read skills commonly return a top-level array, which must not be coerced
            // through JToken.Value<string>() (that throws for JArray).
            return false;
        }

        string? text = scalar.Value<string>();
        return !string.IsNullOrWhiteSpace(text)
            && (text.Contains(" is required", StringComparison.OrdinalIgnoreCase)
                || text.Contains(" failed", StringComparison.OrdinalIgnoreCase)
                || text.Contains("invalid ", StringComparison.OrdinalIgnoreCase)
                || text.Contains("not persist", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryCreateSafeCorrection(
        ChatToolCall call,
        string? result,
        out ChatToolCall? correctedCall)
    {
        correctedCall = null;
        if (ParseResult(result) is not JObject obj
            || obj.GetValue("safeAutomaticCorrection", StringComparison.OrdinalIgnoreCase)?.Value<bool>() != true
            || obj.GetValue("correctedArguments", StringComparison.OrdinalIgnoreCase) is not JObject correctedArguments)
        {
            return false;
        }

        string correctedJson = correctedArguments.ToString(Formatting.None);
        if (Canonicalize(call.FunctionArguments.ToString()) == Canonicalize(correctedJson))
        {
            return false;
        }

        correctedCall = ChatToolCall.CreateFunctionToolCall(
            call.Id,
            call.FunctionName,
            BinaryData.FromString(correctedJson));
        return true;
    }

    internal static string Fingerprint(ChatToolCall call)
    {
        string canonical = $"{call.FunctionName}\n{Canonicalize(call.FunctionArguments.ToString())}";
        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return BitConverter.ToString(digest, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
    }

    internal static string DuplicateFailureResult(string functionName) =>
        JsonConvert.SerializeObject(new
        {
            success = false,
            outcome = "duplicate_failed_request",
            errorCode = "identical_failed_request",
            operation = functionName,
            retryable = false,
            assistantInstruction = "Do not submit the identical payload again. Change the arguments using the prior correction guidance, or disclose that the operation was not saved."
        });

    internal static string DependencyFailureResult(string functionName) =>
        JsonConvert.SerializeObject(new
        {
            success = false,
            outcome = "dependency_failed",
            errorCode = "prior_mutation_failed",
            operation = functionName,
            retryable = true,
            assistantInstruction = "A prior write in this batch failed, so this write was not attempted. Repair and verify the failed operation before continuing."
        });

    internal static string MissingResultFailureResult(string functionName) =>
        JsonConvert.SerializeObject(new
        {
            success = false,
            outcome = "missing_tool_result",
            errorCode = "missing_tool_result",
            operation = functionName,
            retryable = false,
            assistantInstruction = "The tool returned no result. Treat the operation as unverified: do not claim it was completed, saved, or synced. Re-read the affected state before retrying any write."
        });

    internal static string NormalizeResult(string functionName, string? result) =>
        string.IsNullOrWhiteSpace(result)
            ? MissingResultFailureResult(functionName)
            : result;

    static JToken? ParseResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        try
        {
            JToken token = JToken.Parse(result);
            for (int i = 0; i < 2 && token.Type == JTokenType.String; i++)
            {
                string? nested = token.Value<string>();
                if (string.IsNullOrWhiteSpace(nested))
                {
                    break;
                }

                try
                {
                    token = JToken.Parse(nested);
                }
                catch (JsonReaderException)
                {
                    break;
                }
            }

            return token;
        }
        catch (JsonReaderException)
        {
            return new JValue(result);
        }
    }

    static string Canonicalize(string json)
    {
        try
        {
            return SortToken(JToken.Parse(json)).ToString(Formatting.None);
        }
        catch (JsonReaderException)
        {
            return json.Trim();
        }
    }

    static JToken SortToken(JToken token) => token switch
    {
        JObject obj => new JObject(obj.Properties()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => new JProperty(property.Name, SortToken(property.Value)))),
        JArray array => new JArray(array.Select(SortToken)),
        _ => token.DeepClone()
    };

    internal sealed record ToolExecution(
        ChatToolCall Call,
        string? Result,
        ToolExecutionDisposition Disposition);

    internal sealed record DependencyRule(string PrerequisiteOperation, string DependentOperation);

    internal enum ToolExecutionDisposition
    {
        Invoked,
        DuplicateBlocked,
        DependencyBlocked
    }
}
