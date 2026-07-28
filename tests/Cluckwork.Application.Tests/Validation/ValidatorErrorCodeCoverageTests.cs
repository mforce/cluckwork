namespace Cluckwork.Application.Tests.Validation;

using System.Reflection;
using Cluckwork.Application.Common;
using Cluckwork.Application.Tests.Common;
using FluentValidation;
using FluentValidation.Validators;

// #231 — every validation rule must carry an EXPLICIT error code (one that
// contains a '.'), the same discriminator ValidationResponse (#45) uses to tell
// an assigned `Feature.Field.Rule` code from a FluentValidation built-in default
// ("NotEmptyValidator", "PredicateValidator", … — single PascalCase tokens, no
// dot, deliberately dropped from the `errorCodes` contract).
//
// This walks every AbstractValidator<> in Cluckwork.Application and every rule
// COMPONENT it declares, failing if any component's ErrorCode is a framework
// default. So a new rule added without `.WithErrorCode("Feature.Field.Rule")`
// breaks the build rather than silently shipping an uncoded (English-only)
// failure — closing the drift #231 exists to prevent.
public sealed class ValidatorErrorCodeCoverageTests
{
    // One row per concrete validator in the Application assembly, discovered by
    // reflection so a newly-added validator is covered with no edit here.
    public static IEnumerable<object[]> Validators()
    {
        var assembly = typeof(IFarmClock).Assembly; // Cluckwork.Application
        return assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false } && DerivesFromAbstractValidator(t))
            .OrderBy(t => t.Name)
            .Select(t => new object[] { t });
    }

    [Theory]
    [MemberData(nameof(Validators))]
    public void EveryRuleComponentCarriesAnExplicitDottedErrorCode(Type validatorType)
    {
        var validator = (IValidator)Instantiate(validatorType);

        var offenders = CollectLeafComponents(validator, [])
            .Where(x => x.ErrorCode is null || !x.ErrorCode.Contains('.'))
            .Select(x => $"  {x.PropertyName} -> '{x.ErrorCode}'")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{validatorType.Name} has rule(s) without an explicit dotted error code "
                + $"(add .WithErrorCode(\"Feature.Field.Rule\")):\n{string.Join("\n", offenders)}");
    }

    // Every LEAF rule component reachable from `validator`, recursing THROUGH
    // child-validator adaptors (RuleForEach(...).ChildRules and .SetValidator)
    // rather than stopping at them: the wrapper emits no failure of its own, but
    // the rules INSIDE it do and must be coded too. An inline ChildRules validator
    // isn't a discoverable type, so recursion is the only way to guard its rules
    // (skipping the adaptor instead would leave them unchecked — the #231 hole).
    private static IEnumerable<(string PropertyName, string? ErrorCode)> CollectLeafComponents(
        IValidator validator, HashSet<IValidator> visited)
    {
        if (!visited.Add(validator)) yield break; // guard against a self-referential graph
        foreach (var rule in validator.CreateDescriptor().Rules)
            foreach (var component in rule.Components)
            {
                if (component.Validator is IChildValidatorAdaptor)
                {
                    // Recurse into the fixed child validator if we can reach it. A
                    // provider-based SetValidator(ctx => …) exposes no static instance,
                    // but those are named AbstractValidator<> TYPES already covered by
                    // Validators() — so there is nothing to check on the wrapper itself.
                    if (GetChildValidator(component.Validator) is { } child)
                        foreach (var leaf in CollectLeafComponents(child, visited))
                            yield return leaf;
                    continue;
                }
                yield return (rule.PropertyName, component.ErrorCode);
            }
    }

    // ChildRules / SetValidator(instance) stash the fixed child validator in a
    // private field on the adaptor; reflect it so the inline rules are reachable.
    private static IValidator? GetChildValidator(IPropertyValidator adaptor)
    {
        for (var t = adaptor.GetType(); t is not null; t = t.BaseType)
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                if (typeof(IValidator).IsAssignableFrom(field.FieldType) && field.GetValue(adaptor) is IValidator v)
                    return v;
        return null;
    }

    private static bool DerivesFromAbstractValidator(Type type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(AbstractValidator<>))
                return true;
        return false;
    }

    // The only constructor dependency any validator has today is IFarmClock
    // (7 of them, for the farm-local date rules); the rest are parameterless.
    // A new dependency type surfaces here as a clear failure to extend, rather
    // than an opaque reflection error.
    private static object Instantiate(Type validatorType)
    {
        var ctor = validatorType.GetConstructors().Single();
        var args = ctor.GetParameters().Select(object (p) =>
            p.ParameterType == typeof(IFarmClock)
                ? FixedFarmClock.AtDefault()
                : throw new InvalidOperationException(
                    $"{validatorType.Name} needs {p.ParameterType.Name}, which the error-code "
                        + "coverage guard does not know how to supply — add it to Instantiate()."))
            .ToArray();
        return Activator.CreateInstance(validatorType, args)!;
    }
}
