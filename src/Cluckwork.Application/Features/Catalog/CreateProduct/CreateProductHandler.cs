namespace Cluckwork.Application.Features.Catalog.CreateProduct;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;

public sealed class CreateProductHandler(
    IProductRepository products,
    IEggGradeRepository grades,
    IAccountRepository accounts,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateProductCommand command, Guid accountId, CancellationToken ct)
    {
        // Friendly pre-check; the unique lower(Name) index is the real
        // guarantee and races surface as the global 409 mapping.
        if (await products.NameExistsAsync(command.Name, excludeId: null, ct))
            return Result.Failure<Guid>(Error.Conflict(
                "Product.DuplicateName", $"A product named '{command.Name.Trim()}' already exists."));

        // Validator guarantees Egg + a grade id; the grade must exist and be
        // active — mapping a product to a retired bucket would sell from it.
        var grade = await grades.GetByIdAsync(command.EggGradeId!.Value, ct);
        if (grade is null)
            return Result.Failure<Guid>(Error.Validation(
                "Product.UnknownGrade", "The egg grade does not exist."));
        if (!grade.Active)
            return Result.Failure<Guid>(Error.Validation(
                "Product.InactiveGrade", "The egg grade is inactive."));

        // Currency snapshots from the account at creation (spec §16). The
        // snapshot and the insert share a transaction with FOR SHARE on the
        // account row (#162) — a product's first price binds the currency, so
        // it participates in the same lock protocol as the money rows.
        Result<Guid>? outcome = null;
        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            var account = await accounts.GetCurrentSharedLockedAsync(transactionCt);
            if (account is null)
            {
                outcome = Result.Failure<Guid>(Error.NotFound("Account", accountId));
                return false;
            }

            var product = Product.Create(
                Guid.NewGuid(), accountId, SeedDefaults.FarmId,
                command.Name,
                Enum.Parse<ProductType>(command.ProductType, ignoreCase: true),
                Enum.Parse<ProductUnit>(command.DefaultUnit, ignoreCase: true),
                command.DefaultPriceMinorUnits,
                account.DefaultCurrencyCode, account.DefaultCurrencyMinorUnit,
                command.Notes);

            await products.AddAsync(product, transactionCt);
            await products.AddMappingAsync(
                ProductEggGradeMapping.Create(Guid.NewGuid(), accountId, product.Id, grade.Id),
                transactionCt);

            await audit.WriteAsync("Product.Create", nameof(Product), product.Id,
                details: new { product.Name, product.ProductType, EggGrade = grade.Name }, ct: transactionCt);

            outcome = Result.Success(product.Id);
            return true;
        }, ct);

        return outcome!;
    }
}
