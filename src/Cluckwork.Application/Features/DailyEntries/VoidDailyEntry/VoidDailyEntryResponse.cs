using Cluckwork.Application.Common;
using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.Eggs;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Modules.FlockManagement.Flocks;
using Microsoft.Extensions.Logging;

namespace Cluckwork.Application.Features.DailyEntries.VoidDailyEntry;

[ModuleContract("EggOperations")]
public sealed record VoidDailyEntryResponse(Guid Id, string Status, int Version);
