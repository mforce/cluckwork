namespace Cluckwork.Domain.Modules.EggOperations.Contracts;

[ModuleContract("EggOperations")]
public enum DailyEntryStatus { Draft, Submitted, Locked, ManagerAdjusted, Voided }
