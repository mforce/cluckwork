namespace Cluckwork.Application.Features.Catalog.UpdateProduct;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;

public sealed class UpdateProductHandler(
    IProductRepository products,
    IEggGradeRepository grades,
    IAccountRepository accounts,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result> HandleAsync(UpdateProductCommand command, CancellationToken ct)
    {
        var product = await products.GetByIdAsync(command.ProductId, ct);
        if (product is null)
            return Result.Failure(Error.NotFound("Product", command.ProductId));

        if (await products.NameExistsAsync(command.Name, excludeId: product.Id, ct))
            return Result.Failure(Error.Conflict(
                "Product.DuplicateName", $"A product named '{command.Name.Trim()}' already exists."));

        var grade = await grades.GetByIdAsync(command.EggGradeId!.Value, ct);
        if (grade is null)
            return Result.Failure(Error.Validation(
                "Product.UnknownGrade", "The egg grade does not exist."));
        if (!grade.Active)
            return Result.Failure(Error.Validation(
                "Product.InactiveGrade", "The egg grade is inactive."));

        // The currency matters only for the unpriced → priced transition, but
        // that transition binds the farm's currency like any money row, so the
        // read and the write share a transaction with FOR SHARE on the account
        // row (#162). The path is not hot; the uniform lock beats a
        // when-exactly-does-pricing-change analysis that would rot.
        Result? outcome = null;
        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            var account = await accounts.GetCurrentSharedLockedAsync(transactionCt);
            if (account is null)
            {
                outcome = Result.Failure(Error.NotFound("Account", "current"));
                return false;
            }

            // Mapping fetched BEFORE the entity mutation: every exit after
            // product.Update succeeds must commit, or a rolled-back mutation
            // would linger on the tracked entity for a later save in this
            // request (the idempotency record) to flush.
            var mapping = await products.GetMappingAsync(product.Id, transactionCt);
            if (mapping is null)
            {
                outcome = Result.Failure(Error.NotFound("ProductEggGradeMapping", product.Id));
                return false;
            }

            var result = product.Update(
                command.Name,
                Enum.Parse<ProductUnit>(command.DefaultUnit, ignoreCase: true),
                command.DefaultPriceMinorUnits,
                command.Notes,
                account.DefaultCurrencyCode,
                account.DefaultCurrencyMinorUnit);
            if (result.IsFailure)
            {
                outcome = result;
                return false;
            }

            // Re-pointing the grade is safe: sold lines snapshot at sale time
            // (part 2), so history never reinterprets.
            mapping.Repoint(grade.Id);

            await audit.WriteAsync("Product.Update", nameof(Product), product.Id,
                details: new { product.Name, product.DefaultUnit, product.DefaultPriceMinorUnits, EggGrade = grade.Name },
                ct: transactionCt);

            outcome = Result.Success();
            return true;
        }, ct);

        return outcome!;
    }
}
