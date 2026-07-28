namespace Cluckwork.Application.Tests.Validation;

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

        var offenders = validator.CreateDescriptor().Rules
            .SelectMany(rule => rule.Components.Select(component => (rule.PropertyName, component)))
            // A RuleForEach(...).ChildRules / .SetValidator wrapper is a child-validator
            // adaptor: it emits no failure of its own (its LEAF rules do, each with its
            // own explicit code), so its empty wrapper code is not a coverage gap. A
            // child validator declared as its own AbstractValidator<> type is still
            // discovered and checked independently by Validators() above.
            .Where(x => x.component.Validator is not IChildValidatorAdaptor)
            .Where(x => x.component.ErrorCode is null || !x.component.ErrorCode.Contains('.'))
            .Select(x => $"  {x.PropertyName} -> '{x.component.ErrorCode}'")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{validatorType.Name} has rule(s) without an explicit dotted error code "
                + $"(add .WithErrorCode(\"Feature.Field.Rule\")):\n{string.Join("\n", offenders)}");
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
