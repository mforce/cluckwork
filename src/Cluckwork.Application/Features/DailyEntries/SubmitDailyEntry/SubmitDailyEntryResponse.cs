using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.Eggs;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Microsoft.Extensions.Logging;

namespace Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;

[ModuleContract("EggOperations")]
public sealed record SubmitDailyEntryResponse(Guid Id, string Status, IReadOnlyList<Guid> EggLotIds);
