using Cluckwork.Domain.Modules.Commerce.Contracts;

namespace Cluckwork.Application.Common;

public sealed record FarmSignIn(Guid AccountId, bool IsActive);
