// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Host.Bindings;
using Microsoft.Azure.WebJobs.Host.Listeners;
using Microsoft.Azure.WebJobs.Host.Protocols;
using Microsoft.Azure.WebJobs.Host.Triggers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

class AssistantSkillTriggerBindingProvider : ITriggerBindingProvider
{
    static readonly Task<ITriggerBinding?> NullTriggerBindingTask = Task.FromResult<ITriggerBinding?>(null);

    readonly INameResolver nameResolver;
    readonly AssistantSkillManager skillManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssistantSkillTriggerBindingProvider"/> class.
    /// </summary>
    /// <remarks>
    /// This constructor is called by the dependency management container in the Functions (WebJobs) runtime.
    /// </remarks>
    public AssistantSkillTriggerBindingProvider(INameResolver nameResolver, AssistantSkillManager skillManager)
    {
        this.nameResolver = nameResolver ?? throw new ArgumentNullException(nameof(nameResolver));
        this.skillManager = skillManager;
    }

    public Task<ITriggerBinding?> TryCreateAsync(TriggerBindingProviderContext context)
    {
        // The function trigger parameter must have AssistantTriggerAttribute
        ParameterInfo parameter = context.Parameter;
        AssistantSkillTriggerAttribute? attribute = parameter.GetCustomAttribute<AssistantSkillTriggerAttribute>();
        if (attribute is null)
        {
            return NullTriggerBindingTask;
        }

        string functionName = GetFunctionName(parameter, this.nameResolver, attribute.FunctionName);
        ITriggerBinding binding = new AssistantTriggerBinding(
            functionName,
            attribute,
            parameter,
            this.skillManager);
        return Task.FromResult(binding)!;
    }

    /// <summary>
    /// Gets the appropriate name of the assistant function.
    /// </summary>
    static string GetFunctionName(ParameterInfo parameter, INameResolver nameResolver, string? triggerName)
    {
        if (triggerName is null)
        {
            MemberInfo method = parameter.Member;
            return method.GetCustomAttribute<FunctionNameAttribute>()?.Name ?? method.Name;
        }
        else if (nameResolver.TryResolveWholeString(triggerName, out string? resolvedTriggerName))
        {
            return resolvedTriggerName;
        }
        else
        {
            return triggerName;
        }
    }

    class AssistantTriggerBinding : ITriggerBinding
    {
        readonly string skillName;
        readonly AssistantSkillTriggerAttribute attribute;
        readonly ParameterInfo parameterInfo;
        readonly AssistantSkillManager skillManager;

        public AssistantTriggerBinding(
            string skillName,
            AssistantSkillTriggerAttribute attribute,
            ParameterInfo parameterInfo,
            AssistantSkillManager skillManager)
        {
            this.skillName = skillName ?? throw new ArgumentNullException(nameof(skillName));
            this.attribute = attribute ?? throw new ArgumentNullException(nameof(attribute));
            this.parameterInfo = parameterInfo ?? throw new ArgumentNullException(nameof(parameterInfo));
            this.skillManager = skillManager ?? throw new ArgumentNullException(nameof(skillManager));
        }

        public Type TriggerValueType => typeof(SkillInvocationContext);

        public IReadOnlyDictionary<string, Type> BindingDataContract { get; } =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
            {
                // This binding supports return values of any type
                { "$return", typeof(object).MakeByRefType() },
            };

        public ParameterDescriptor ToParameterDescriptor()
        {
            return new ParameterDescriptor { Name = this.parameterInfo.Name };
        }

        /// <summary>
        /// Converts the trigger value (from the OpenAI model) into the type of the function trigger parameter.
        /// NOTE: Need to review whether this logic will work correctly for out-of-proc languages.
        /// </summary>
        public Task<ITriggerData> BindAsync(object value, ValueBindingContext context)
        {
            Type destinationType = this.parameterInfo.ParameterType;

            SkillInvocationContext skillInvocationContext = (SkillInvocationContext)value;

            object? convertedValue;
            if (!string.IsNullOrEmpty(skillInvocationContext.Arguments?.ToString()))
            {
                // Prefer the named parameter, but accept direct typed JSON for string-backed skills.
                // The direct form is the public contract; {"input":"{...}"} remains supported while
                // callers migrate away from the legacy serialized wrapper.
                JObject argsJson = JObject.Parse(skillInvocationContext.Arguments.ToString());
                JToken? paramValue = argsJson[this.parameterInfo.Name];
                convertedValue = AssistantSkillArgumentNormalizer.Convert(
                    argsJson,
                    paramValue,
                    destinationType);
            }
            else
            {
                // Value types in .NET can't be assigned to null, so we use Activator.CreateInstance to 
                // create a default value of the type (example, 0 for int).
                convertedValue = destinationType.IsValueType ? Activator.CreateInstance(destinationType) : null;
            }

            SimpleValueProvider inputValueProvider = new(convertedValue, destinationType);

            Dictionary<string, object?> bindingData = new(StringComparer.OrdinalIgnoreCase); // TODO: Cache
            TriggerData triggerData = new(inputValueProvider, bindingData);
            return Task.FromResult<ITriggerData>(triggerData);
        }

        public Task<IListener> CreateListenerAsync(ListenerFactoryContext context)
        {
            IListener listener = new AssistantTriggerListener(
                this.skillName,
                this.attribute,
                this.parameterInfo,
                context,
                this.skillManager);
            return Task.FromResult(listener);
        }

        class SimpleValueProvider : IValueProvider
        {
            readonly object? value;
            readonly Task<object?> valueAsTask;
            readonly Type valueType;

            public SimpleValueProvider(object? value, Type valueType)
            {
                if (value is not null && !valueType.IsAssignableFrom(value.GetType()))
                {
                    throw new ArgumentException($"Cannot convert {value} to {valueType.Name}.");
                }

                this.value = value;
                this.valueAsTask = Task.FromResult(value);
                this.valueType = valueType;
            }

            public Type Type
            {
                get { return this.valueType; }
            }

            public Task<object?> GetValueAsync()
            {
                return this.valueAsTask;
            }

            public string? ToInvokeString()
            {
                return this.value?.ToString();
            }
        }

        class AssistantTriggerListener : IListener
        {
            readonly string skillName;
            readonly AssistantSkillTriggerAttribute attribute;
            readonly ParameterInfo parameterInfo;
            readonly ListenerFactoryContext context;
            readonly AssistantSkillManager skillManager;

            public AssistantTriggerListener(
                string skillName,
                AssistantSkillTriggerAttribute attribute,
                ParameterInfo parameterInfo,
                ListenerFactoryContext context,
                AssistantSkillManager skillManager)
            {
                this.skillName = skillName;
                this.attribute = attribute;
                this.parameterInfo = parameterInfo;
                this.context = context;
                this.skillManager = skillManager;
            }

            public Task StartAsync(CancellationToken cancellationToken)
            {
                this.skillManager.RegisterSkill(
                    this.skillName,
                    this.attribute,
                    this.parameterInfo,
                    this.context.Executor);
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken)
            {
                this.skillManager.UnregisterSkill(this.skillName);
                return Task.CompletedTask;
            }

            public void Cancel()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}

internal static class AssistantSkillArgumentNormalizer
{
    internal static object? Convert(JObject arguments, JToken? namedValue, Type destinationType)
    {
        if (destinationType != typeof(string))
        {
            return namedValue?.ToObject(destinationType);
        }

        if (namedValue is null)
        {
            return arguments.ToString(Formatting.None);
        }

        return namedValue.Type == JTokenType.String
            ? NormalizeSerializedJson(namedValue.Value<string>())
            : namedValue.ToString(Formatting.None);
    }

    static string? NormalizeSerializedJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        string candidate = value.Trim();
        if (candidate[0] is not ('{' or '['))
        {
            return value;
        }

        for (int removedDelimiters = 0; removedDelimiters <= 2; removedDelimiters++)
        {
            try
            {
                return JToken.Parse(candidate).ToString(Formatting.None);
            }
            catch (JsonReaderException) when (
                removedDelimiters < 2 &&
                candidate.Length > 0 &&
                candidate[^1] is '}' or ']')
            {
                // Older assistants sometimes wrap typed arguments in a serialized `input`
                // string and append a duplicate closing delimiter. Only remove a trailing
                // delimiter when the current value is invalid and the shorter value parses.
                candidate = candidate[..^1].TrimEnd();
            }
            catch (JsonReaderException)
            {
                return value;
            }
        }

        return value;
    }
}
