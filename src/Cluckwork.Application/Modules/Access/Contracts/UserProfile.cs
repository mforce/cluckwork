using Cluckwork.Domain.Modules.Commerce.Contracts;

namespace Cluckwork.Application.Modules.Access.Contracts;

public sealed record UserProfile(
    Guid Id, string Email, string? DisplayName, string Role, string? Language,
    EggUnit? PreferredStepperUnit);
