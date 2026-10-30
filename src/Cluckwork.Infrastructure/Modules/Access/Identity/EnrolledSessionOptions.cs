namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 decision 8: an enrolled user's refresh lineage ends this many days
// after its last full sign-in, so an active session still asks for the code.
// A whole number from 1 to 365, checked when a serving process boots.
public sealed class EnrolledSessionOptions
{
    public const string SectionName = "Auth";
    public const int MinDays = 1;
    public const int MaxDays = 365;

    public int EnrolledSessionMaxDays { get; init; } = 30;
}
