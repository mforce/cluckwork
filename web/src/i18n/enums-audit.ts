// Audit (AuditPage) label families: actions, their recorded entity types and
// the entity-type vocabulary.
// See the module header in ./enums.ts for the compile-time scheme
// (VALUES tuple -> KEYS map -> label function) and ./enums-key.ts for the
// key-constraint type.
import i18n from "./index";
import type { EnumsKey } from "./enums-key";

// ---------------------------------------------------------------------------
// audit action (AuditPage filter + table) — AuditEvent.action, a server
// "Entity.Verb" capture-point code (#182, Task 29). This is also the single
// source of truth for the filter's option list — AuditPage no longer keeps
// its own local `actions` array (part of the #84 magic-strings debt).
// ---------------------------------------------------------------------------
export const AUDIT_ACTION_VALUES = [
  "DailyEntry.Adjust", "DailyEntry.Void", "SalesOrder.Void", "Payment.Void",
  "Expense.Adjust", "ExpenseCategory.Update", "InventoryItem.Adjust",
  "WaterUsage.Correct", "Flock.BirdMovement", "Flock.Update", "Flock.Deplete",
  "Flock.Archive", "Flock.Reactivate", "EggGrade.Update", "EggGrade.Activate",
  "EggGrade.Deactivate", "User.Create", "User.Update", "User.PasswordSet",
  "User.PasswordChanged", "User.BreakGlassReset", "User.RoleChanged", "User.EmailChanged", "User.Disabled", "User.Enabled",
  "User.FlockAssign", "User.FlockUnassign", "User.AppConnected", "User.AppReconnected", "User.AppDisconnected",
  "Account.Export", "Account.SetLogo", "Account.RemoveLogo",
  "Account.SetBanner", "Account.RemoveBanner", "Account.UpdateSettings",
  "Account.Suspend", "Account.Reactivate", "Account.Provisioned", "Account.Rename",
  "Product.Create", "Product.Update", "Product.Activate",
  "Product.Deactivate", "EggUnitConversion.Update", "EggLot.Movement",
  "Flock.Create", "DailyEntry.Create", "DailyEntry.Update", "DailyEntry.Submit",
  "SalesOrder.Create", "SalesOrder.Confirm", "SalesOrder.Cancel",
  "SalesOrder.AddItem", "SalesOrder.UpdateItem", "SalesOrder.RemoveItem",
  "Expense.Create", "FeedUsage.Create", "WaterUsage.Create", "InventoryItem.Purchase",
  "EggGrade.Create", "Customer.Create", "Customer.Update",
] as const;
export type AuditActionValue = (typeof AUDIT_ACTION_VALUES)[number];
export const AUDIT_ACTION_KEYS = {
  "DailyEntry.Adjust": "enums:auditAction.DailyEntry.Adjust",
  "DailyEntry.Void": "enums:auditAction.DailyEntry.Void",
  "SalesOrder.Void": "enums:auditAction.SalesOrder.Void",
  "Payment.Void": "enums:auditAction.Payment.Void",
  "Expense.Adjust": "enums:auditAction.Expense.Adjust",
  "ExpenseCategory.Update": "enums:auditAction.ExpenseCategory.Update",
  "InventoryItem.Adjust": "enums:auditAction.InventoryItem.Adjust",
  "WaterUsage.Correct": "enums:auditAction.WaterUsage.Correct",
  "Flock.BirdMovement": "enums:auditAction.Flock.BirdMovement",
  "Flock.Update": "enums:auditAction.Flock.Update",
  "Flock.Deplete": "enums:auditAction.Flock.Deplete",
  "Flock.Archive": "enums:auditAction.Flock.Archive",
  "Flock.Reactivate": "enums:auditAction.Flock.Reactivate",
  "EggGrade.Update": "enums:auditAction.EggGrade.Update",
  "EggGrade.Activate": "enums:auditAction.EggGrade.Activate",
  "EggGrade.Deactivate": "enums:auditAction.EggGrade.Deactivate",
  "User.Create": "enums:auditAction.User.Create",
  "User.Update": "enums:auditAction.User.Update",
  "User.PasswordSet": "enums:auditAction.User.PasswordSet",
  "User.PasswordChanged": "enums:auditAction.User.PasswordChanged",
  "User.BreakGlassReset": "enums:auditAction.User.BreakGlassReset",
  "User.RoleChanged": "enums:auditAction.User.RoleChanged",
  "User.EmailChanged": "enums:auditAction.User.EmailChanged",
  "User.Disabled": "enums:auditAction.User.Disabled",
  "User.Enabled": "enums:auditAction.User.Enabled",
  "User.FlockAssign": "enums:auditAction.User.FlockAssign",
  "User.FlockUnassign": "enums:auditAction.User.FlockUnassign",
  "User.AppConnected": "enums:auditAction.User.AppConnected",
  "User.AppReconnected": "enums:auditAction.User.AppReconnected",
  "User.AppDisconnected": "enums:auditAction.User.AppDisconnected",
  "Account.Export": "enums:auditAction.Account.Export",
  "Account.SetLogo": "enums:auditAction.Account.SetLogo",
  "Account.RemoveLogo": "enums:auditAction.Account.RemoveLogo",
  "Account.SetBanner": "enums:auditAction.Account.SetBanner",
  "Account.RemoveBanner": "enums:auditAction.Account.RemoveBanner",
  "Account.UpdateSettings": "enums:auditAction.Account.UpdateSettings",
  "Account.Suspend": "enums:auditAction.Account.Suspend",
  "Account.Reactivate": "enums:auditAction.Account.Reactivate",
  "Account.Provisioned": "enums:auditAction.Account.Provisioned",
  "Account.Rename": "enums:auditAction.Account.Rename",
  "Product.Create": "enums:auditAction.Product.Create",
  "Product.Update": "enums:auditAction.Product.Update",
  "Product.Activate": "enums:auditAction.Product.Activate",
  "Product.Deactivate": "enums:auditAction.Product.Deactivate",
  "EggUnitConversion.Update": "enums:auditAction.EggUnitConversion.Update",
  "EggLot.Movement": "enums:auditAction.EggLot.Movement",
  "Flock.Create": "enums:auditAction.Flock.Create",
  "DailyEntry.Create": "enums:auditAction.DailyEntry.Create",
  "DailyEntry.Update": "enums:auditAction.DailyEntry.Update",
  "DailyEntry.Submit": "enums:auditAction.DailyEntry.Submit",
  "SalesOrder.AddItem": "enums:auditAction.SalesOrder.AddItem",
  "SalesOrder.UpdateItem": "enums:auditAction.SalesOrder.UpdateItem",
  "SalesOrder.RemoveItem": "enums:auditAction.SalesOrder.RemoveItem",
  "SalesOrder.Create": "enums:auditAction.SalesOrder.Create",
  "SalesOrder.Confirm": "enums:auditAction.SalesOrder.Confirm",
  "SalesOrder.Cancel": "enums:auditAction.SalesOrder.Cancel",
  "Expense.Create": "enums:auditAction.Expense.Create",
  "FeedUsage.Create": "enums:auditAction.FeedUsage.Create",
  "WaterUsage.Create": "enums:auditAction.WaterUsage.Create",
  "InventoryItem.Purchase": "enums:auditAction.InventoryItem.Purchase",
  "EggGrade.Create": "enums:auditAction.EggGrade.Create",
  "Customer.Create": "enums:auditAction.Customer.Create",
  "Customer.Update": "enums:auditAction.Customer.Update",
} as const satisfies Record<AuditActionValue, EnumsKey>;
export function auditActionLabel(value: AuditActionValue | (string & {})): string {
  const key = AUDIT_ACTION_KEYS[value as AuditActionValue];
  return key ? i18n.t(key) : String(value);
}

// Which EntityType each action is actually recorded against server-side —
// read off every audit.WriteAsync(...) call site, not derived from the
// action's own "Entity.Verb" prefix, which is misleading for the four
// Account.Set/RemoveLogo/Banner actions (prefixed "Account", recorded
// against "FarmLogo" — a shared row FarmLogo.cs documents as covering both
// logo and banner). Powers AuditPage's entity-type dropdown, which narrows
// the action dropdown to only actions that can occur on the selected type.
export const AUDIT_ACTION_ENTITY_TYPE = {
  "DailyEntry.Adjust": "DailyEntry",
  "DailyEntry.Void": "DailyEntry",
  "SalesOrder.Void": "SalesOrder",
  "Payment.Void": "Payment",
  "Expense.Adjust": "Expense",
  "ExpenseCategory.Update": "ExpenseCategory",
  "InventoryItem.Adjust": "InventoryItem",
  "WaterUsage.Correct": "WaterUsage",
  "Flock.BirdMovement": "Flock",
  "Flock.Update": "Flock",
  "Flock.Deplete": "Flock",
  "Flock.Archive": "Flock",
  "Flock.Reactivate": "Flock",
  "EggGrade.Update": "EggGrade",
  "EggGrade.Activate": "EggGrade",
  "EggGrade.Deactivate": "EggGrade",
  "User.Create": "User",
  "User.Update": "User",
  "User.PasswordSet": "User",
  "User.PasswordChanged": "User",
  "User.BreakGlassReset": "User",
  "User.RoleChanged": "User",
  "User.EmailChanged": "User",
  "User.Disabled": "User",
  "User.Enabled": "User",
  "User.FlockAssign": "User",
  "User.FlockUnassign": "User",
  "User.AppConnected": "User",
  "User.AppReconnected": "User",
  "User.AppDisconnected": "User",
  "Account.Export": "Account",
  "Account.SetLogo": "FarmLogo",
  "Account.RemoveLogo": "FarmLogo",
  "Account.SetBanner": "FarmLogo",
  "Account.RemoveBanner": "FarmLogo",
  "Account.UpdateSettings": "Account",
  "Account.Suspend": "Account",
  "Account.Reactivate": "Account",
  "Account.Provisioned": "Account",
  "Account.Rename": "Account",
  "Product.Create": "Product",
  "Product.Update": "Product",
  "Product.Activate": "Product",
  "Product.Deactivate": "Product",
  "EggUnitConversion.Update": "EggUnitConversion",
  "EggLot.Movement": "EggLot",
  "Flock.Create": "Flock",
  "DailyEntry.Create": "DailyEntry",
  "DailyEntry.Update": "DailyEntry",
  "DailyEntry.Submit": "DailyEntry",
  "SalesOrder.Create": "SalesOrder",
  "SalesOrder.Confirm": "SalesOrder",
  "SalesOrder.Cancel": "SalesOrder",
  "SalesOrder.AddItem": "SalesOrder",
  "SalesOrder.UpdateItem": "SalesOrder",
  "SalesOrder.RemoveItem": "SalesOrder",
  "Expense.Create": "Expense",
  "FeedUsage.Create": "FeedUsage",
  "WaterUsage.Create": "WaterUsage",
  "InventoryItem.Purchase": "InventoryItem",
  "EggGrade.Create": "EggGrade",
  "Customer.Create": "Customer",
  "Customer.Update": "Customer",
} as const satisfies Record<AuditActionValue, EntityTypeValue>;

// ---------------------------------------------------------------------------
// entity type (AuditPage table entity cell) — AuditEvent.entityType.
// ---------------------------------------------------------------------------
export const ENTITY_TYPE_VALUES = [
  "Account", "Customer", "DailyEntry", "EggGrade", "EggLot", "EggUnitConversion", "Expense",
  "ExpenseCategory", "FarmLogo", "FeedUsage", "Flock", "InventoryItem", "Payment", "Product",
  "SalesOrder", "User", "WaterUsage",
] as const;
export type EntityTypeValue = (typeof ENTITY_TYPE_VALUES)[number];
export const ENTITY_TYPE_KEYS = {
  Account: "enums:entityType.Account",
  Customer: "enums:entityType.Customer",
  DailyEntry: "enums:entityType.DailyEntry",
  EggGrade: "enums:entityType.EggGrade",
  EggLot: "enums:entityType.EggLot",
  EggUnitConversion: "enums:entityType.EggUnitConversion",
  Expense: "enums:entityType.Expense",
  ExpenseCategory: "enums:entityType.ExpenseCategory",
  FarmLogo: "enums:entityType.FarmLogo",
  FeedUsage: "enums:entityType.FeedUsage",
  Flock: "enums:entityType.Flock",
  InventoryItem: "enums:entityType.InventoryItem",
  Payment: "enums:entityType.Payment",
  Product: "enums:entityType.Product",
  SalesOrder: "enums:entityType.SalesOrder",
  User: "enums:entityType.User",
  WaterUsage: "enums:entityType.WaterUsage",
} as const satisfies Record<EntityTypeValue, EnumsKey>;
export function entityTypeLabel(value: EntityTypeValue | (string & {})): string {
  const key = ENTITY_TYPE_KEYS[value as EntityTypeValue];
  return key ? i18n.t(key) : String(value);
}
