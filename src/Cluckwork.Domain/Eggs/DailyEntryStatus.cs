using System.Text.Json;

namespace Cluckwork.Domain.Eggs;

[ModuleContract("EggOperations")]
public enum DailyEntryStatus { Draft, Submitted, Locked, ManagerAdjusted, Voided }
