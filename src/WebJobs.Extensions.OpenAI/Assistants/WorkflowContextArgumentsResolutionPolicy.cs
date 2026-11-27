// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections;
using System.Reflection;
using Microsoft.Azure.WebJobs.Host.Bindings;
using Microsoft.Azure.WebJobs.Host.Bindings.Path;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

/// <summary>
/// Projects selected server request headers into an opaque context envelope.
/// Missing credentials must reach the authorization policy, rather than fail
/// binding resolution before the context operation can deny the request.
/// </summary>
// AutoResolve.ResolutionPolicyType requires this WebJobs extension interface;
// the SDK marks it as prerelease, as in Azure's own resolution policy implementations.
#pragma warning disable CS0618
public sealed class WorkflowContextArgumentsResolutionPolicy : IResolutionPolicy
#pragma warning restore CS0618
{
    /// <inheritdoc />
    public string TemplateBind(PropertyInfo propInfo, Attribute attribute, BindingTemplate template, IReadOnlyDictionary<string, object> bindingData)
    {
        if (!template.Pattern.Equals("{headers}", StringComparison.OrdinalIgnoreCase)) return template.Bind(bindingData);
        var roots = bindingData.Where(pair => pair.Key.Equals("headers", StringComparison.OrdinalIgnoreCase)).ToList();
        var headers = roots.Count == 1 ? HeaderRows(roots[0].Value).ToList() : new List<KeyValuePair<string, object?>>();
        var selected = new JObject
        {
            ["x-ms-client-principal"] = Selected(headers, "x-ms-client-principal"),
            ["authorization"] = Selected(headers, "authorization")
        };
        return selected.ToString(Formatting.None);
    }

    static string? Selected(IReadOnlyList<KeyValuePair<string, object?>> headers, string name)
    {
        var values = headers.Where(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (values.Count != 1) return null;
        string? value = values[0].Value switch
        {
            string text => text,
            JValue { Type: JTokenType.String } token => token.Value<string>(),
            JArray { Count: 1 } array when array[0].Type == JTokenType.String => array[0].Value<string>(),
            IEnumerable<string> items when items.Take(2).Count() == 1 => items.First(),
            _ => null
        };
        return value?.Length <= 16384 ? value : null;
    }

    static IEnumerable<KeyValuePair<string, object?>> HeaderRows(object? headers) => headers switch
    {
        JObject json => json.Properties().Select(p => new KeyValuePair<string, object?>(p.Name, p.Value)),
        IEnumerable<KeyValuePair<string, string>> strings => strings.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)),
        IEnumerable<KeyValuePair<string, object>> values => values.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)),
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> arrays => arrays.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)),
        IDictionary dictionary => dictionary.Cast<DictionaryEntry>().Where(p => p.Key is string).Select(p => new KeyValuePair<string, object?>((string)p.Key, p.Value)),
        _ => Enumerable.Empty<KeyValuePair<string, object?>>()
    };
}
