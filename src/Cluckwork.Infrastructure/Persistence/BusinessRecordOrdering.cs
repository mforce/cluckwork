namespace Cluckwork.Infrastructure.Persistence;

using System.Linq.Expressions;
using Cluckwork.Domain.Common;
using Microsoft.EntityFrameworkCore;

internal static class BusinessRecordOrdering
{
    private const string SequenceProperty = "Sequence";

    public static IOrderedQueryable<T> OrderByBusinessChronology<T>(
        this IQueryable<T> query,
        Expression<Func<T, DateOnly>> businessDate)
        where T : class, ICreatedRecord =>
        query.OrderBy(businessDate)
            .ThenBy(record => record.CreatedAtUtc)
            .ThenBy(record => EF.Property<long>(record, SequenceProperty));

    public static IOrderedQueryable<T> OrderByBusinessChronologyDescending<T>(
        this IQueryable<T> query,
        Expression<Func<T, DateOnly>> businessDate)
        where T : class, ICreatedRecord =>
        query.OrderByDescending(businessDate)
            .ThenByDescending(record => record.CreatedAtUtc)
            .ThenByDescending(record => EF.Property<long>(record, SequenceProperty));

    public static IOrderedQueryable<T> OrderByCreationChronology<T>(this IQueryable<T> query)
        where T : class, ICreatedRecord =>
        query.OrderBy(record => record.CreatedAtUtc)
            .ThenBy(record => EF.Property<long>(record, SequenceProperty));

    public static IOrderedQueryable<T> OrderByCreationChronologyDescending<T>(this IQueryable<T> query)
        where T : class, ICreatedRecord =>
        query.OrderByDescending(record => record.CreatedAtUtc)
            .ThenByDescending(record => EF.Property<long>(record, SequenceProperty));
}
