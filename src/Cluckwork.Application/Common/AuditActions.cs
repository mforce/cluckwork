namespace Cluckwork.Application.Common;

// #258 — stable audit action identifiers, hoisted out of inline string
// literals at every audit.WriteAsync(...) call site so the vocabulary is
// machine-enumerable (AuditVocabularyCoverageTests reflects over this class)
// rather than re-derived by grepping call sites, which cannot see a value
// that reaches WriteAsync through a variable (IdentityProvider's
// ResetPasswordAndRevokeAsync takes its action as a parameter — the literal
// lives at ITS two callers, not at the WriteAsync call itself).
//
// This is the SPA's contract, not just an internal convention: AuditPage's
// action filter is driven by web/src/i18n/enums.ts's AUDIT_ACTION_VALUES,
// which must be a 1:1 match with every value emitted here (#247 was the
// symptom of it drifting; this + the coverage test is the prevention #247
// asked for). Treat this list like a public API: add, never rename or
// repurpose an existing value — a rename desyncs any saved filter/dashboard
// built against the old string.
public static class AuditActions
{
    public const string DailyEntryAdjust = "DailyEntry.Adjust";
    public const string DailyEntryVoid = "DailyEntry.Void";
    public const string SalesOrderVoid = "SalesOrder.Void";
    public const string PaymentVoid = "Payment.Void";
    public const string ExpenseAdjust = "Expense.Adjust";
    public const string ExpenseCategoryUpdate = "ExpenseCategory.Update";
    public const string InventoryItemAdjust = "InventoryItem.Adjust";
    public const string WaterUsageCorrect = "WaterUsage.Correct";
    public const string FlockBirdMovement = "Flock.BirdMovement";
    public const string FlockUpdate = "Flock.Update";
    public const string FlockDeplete = "Flock.Deplete";
    public const string FlockArchive = "Flock.Archive";
    public const string FlockReactivate = "Flock.Reactivate";
    public const string EggGradeUpdate = "EggGrade.Update";
    public const string EggGradeActivate = "EggGrade.Activate";
    public const string EggGradeDeactivate = "EggGrade.Deactivate";
    public const string UserCreate = "User.Create";
    public const string UserUpdate = "User.Update";
    public const string UserPasswordSet = "User.PasswordSet";
    public const string UserPasswordChanged = "User.PasswordChanged";
    public const string UserBreakGlassReset = "User.BreakGlassReset";
    public const string UserFlockAssign = "User.FlockAssign";
    public const string UserFlockUnassign = "User.FlockUnassign";
    public const string AccountExport = "Account.Export";
    public const string AccountSetLogo = "Account.SetLogo";
    public const string AccountRemoveLogo = "Account.RemoveLogo";
    public const string AccountUpdateSettings = "Account.UpdateSettings";
    public const string ProductCreate = "Product.Create";
    public const string ProductUpdate = "Product.Update";
    public const string ProductActivate = "Product.Activate";
    public const string ProductDeactivate = "Product.Deactivate";
    public const string EggUnitConversionUpdate = "EggUnitConversion.Update";
    public const string EggLotMovement = "EggLot.Movement";
}
