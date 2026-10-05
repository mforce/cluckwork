using Cluckwork.Domain.Modules.Commerce.Contracts;

namespace Cluckwork.Application.Common;

public sealed record UserProfile(
    Guid Id, string Email, string? DisplayName, string Role, string? Language,
    EggUnit? PreferredStepperUnit);
