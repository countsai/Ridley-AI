using System.Reflection;
using Ridley.Core;

namespace Ridley.Core.Tests;

/// <summary>Architecture rules from the CTO review, enforced mechanically so they cannot silently regress.</summary>
public class ContractRulesTests
{
    private static IEnumerable<Type> ContractInterfaces =>
        typeof(IModelProvider).Assembly.GetExportedTypes().Where(t => t.IsInterface);

    [Fact]
    public void Every_async_contract_method_accepts_a_CancellationToken()
    {
        var offenders = new List<string>();
        foreach (var type in ContractInterfaces)
        {
            foreach (var method in type.GetMethods())
            {
                var isAsync = typeof(Task).IsAssignableFrom(method.ReturnType)
                    || (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
                if (isAsync && !method.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken)))
                {
                    offenders.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Executor_only_accepts_gate_authorised_steps()
    {
        var parameterTypes = typeof(IActionExecutor).GetMethods().SelectMany(m => m.GetParameters()).Select(p => p.ParameterType);
        Assert.Contains(typeof(AuthorisedStep), parameterTypes);
        Assert.DoesNotContain(typeof(PlannedStep), parameterTypes);
    }

    [Fact]
    public void Verifier_is_also_tied_to_authorised_steps()
    {
        var parameterTypes = typeof(IActionVerifier).GetMethods().SelectMany(m => m.GetParameters()).Select(p => p.ParameterType);
        Assert.DoesNotContain(typeof(PlannedStep), parameterTypes);
    }

    [Fact]
    public void AuthorisedStep_and_ValidatedArgs_have_no_public_constructors()
    {
        Assert.Empty(typeof(AuthorisedStep).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(ValidatedArgs).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void PlannedStep_cannot_carry_raw_model_arguments()
    {
        var parameterTypes = typeof(PlannedStep).GetConstructors().Single().GetParameters().Select(p => p.ParameterType);
        Assert.Contains(typeof(ValidatedArgs), parameterTypes);
        Assert.DoesNotContain(typeof(System.Text.Json.JsonElement), parameterTypes);
    }

    [Fact]
    public void Screen_and_audio_payloads_do_not_leak_content_through_ToString()
    {
        var capture = new ScreenCapture([1, 2, 3, 4], new Rect(0, 0, 1920, 1080), DateTimeOffset.UtcNow);
        var audio = new AudioClip([9, 9, 9], 16000, 1);
        Assert.Contains("redacted", capture.ToString());
        Assert.DoesNotContain("9, 9", audio.ToString());
    }
}
