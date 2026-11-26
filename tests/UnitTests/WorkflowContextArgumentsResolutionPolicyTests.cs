// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;
using Microsoft.Azure.WebJobs.Host.Bindings.Path;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Tests.Assistants;

public class WorkflowContextArgumentsResolutionPolicyTests
{
    [Fact]
    public void MissingHeadersProduceAnEmptySelectedHeaderEnvelope()
    {
        var binding = new Dictionary<string, object>();
        Assert.Throws<InvalidOperationException>(() => BindingTemplate.FromString("{headers.x-ms-client-principal}").Bind(binding));
        var result = JObject.Parse(Bind("{headers}", binding));
        Assert.Equal(JTokenType.Null, result["x-ms-client-principal"]!.Type);
        Assert.Equal(JTokenType.Null, result["authorization"]!.Type);
    }

    [Fact]
    public void OnlySelectedCredentialsAreSerializedFromHostHeaderObjects()
    {
        var binding = new Dictionary<string, object> { ["headers"] = new JObject
        {
            ["X-MS-CLIENT-PRINCIPAL"] = "principal-base64", ["Authorization"] = "Bearer opaque-token", ["Cookie"] = "private-cookie"
        } };
        var result = JObject.Parse(Bind("{headers}", binding));
        Assert.Equal("principal-base64", result["x-ms-client-principal"]);
        Assert.Equal("Bearer opaque-token", result["authorization"]);
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain("private-cookie", result.ToString());
    }

    [Fact]
    public void DuplicateOrMultipleCredentialHeadersAreRejected()
    {
        var binding = new Dictionary<string, object> { ["headers"] = new JObject
        {
            ["x-ms-client-principal"] = "first", ["X-MS-CLIENT-PRINCIPAL"] = "second",
            ["authorization"] = new JArray("Bearer first", "Bearer second")
        } };
        var result = JObject.Parse(Bind("{headers}", binding));
        Assert.Equal(JTokenType.Null, result["x-ms-client-principal"]!.Type);
        Assert.Equal(JTokenType.Null, result["authorization"]!.Type);
    }

    [Fact]
    public void StringDictionariesAndAbsentSelectedHeadersAreSupported()
    {
        var binding = new Dictionary<string, object> { ["headers"] = new Dictionary<string, string> { ["authorization"] = "Bearer token", ["Cookie"] = "secret" } };
        var result = JObject.Parse(Bind("{headers}", binding));
        Assert.Equal("Bearer token", result["authorization"]);
        Assert.Equal(JTokenType.Null, result["x-ms-client-principal"]!.Type);
    }

    [Fact]
    public void OtherTemplatesKeepNormalWebJobsResolution()
    {
        Assert.Equal("opaque-value", Bind("{context}", new Dictionary<string, object> { ["context"] = "opaque-value" }));
        Assert.Equal("static", Bind("static", new Dictionary<string, object>()));
    }

    static string Bind(string template, IReadOnlyDictionary<string, object> data) => new WorkflowContextArgumentsResolutionPolicy().TemplateBind(
        typeof(AssistantPostAttribute).GetProperty(nameof(AssistantPostAttribute.WorkflowContextArguments))!,
        new AssistantPostAttribute("assistant", "message"), BindingTemplate.FromString(template), data);
}
