namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Expenses;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ExpenseCategoryConfiguration : IEntityTypeConfiguration<ExpenseCategory>
{
    public void Configure(EntityTypeBuilder<ExpenseCategory> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.AccountId).IsRequired();
        builder.Property(c => c.FarmId).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(ExpenseCategory.MaxNameLength).IsRequired();
        builder.Property(c => c.Version).IsConcurrencyToken();

        // Name uniqueness is case-insensitive per farm — enforced by a raw
        // lower(Name) expression index (EF can't model it); see the InitialCreate
        // migration (#245 squashed the AddExpenses one that introduced it).
        // Handlers pre-check via NameExistsAsync for a friendly 409;
        // the index is the real guarantee (grade-catalog pattern).
    }
}

public sealed class ExpenseConfiguration : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.AccountId).IsRequired();
        builder.Property(e => e.FarmId).IsRequired();
        builder.Property(e => e.ExpenseCategoryId).IsRequired();
        builder.Property(e => e.Date).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(Expense.MaxDescriptionLength).IsRequired();
        builder.Property(e => e.AmountMinorUnits).IsRequired();
        builder.Property(e => e.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(e => e.CurrencyMinorUnit).IsRequired();
        builder.Property(e => e.Note).HasMaxLength(Expense.MaxNoteLength);
        builder.Property(e => e.Version).IsConcurrencyToken();

        // Categories with recorded expenses must not disappear.
        builder.HasOne<ExpenseCategory>()
            .WithMany()
            .HasForeignKey(e => e.ExpenseCategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // The optional flock link must never dangle (feed/water pattern).
        builder.HasOne<Cluckwork.Domain.Flocks.Flock>()
            .WithMany()
            .HasForeignKey(e => e.FlockId)
            .OnDelete(DeleteBehavior.Restrict);

        // The list/sum screens filter by tenant + date (+ category).
        builder.HasIndex(e => new { e.AccountId, e.Date });
        builder.HasIndex(e => new { e.AccountId, e.ExpenseCategoryId });
    }
}
