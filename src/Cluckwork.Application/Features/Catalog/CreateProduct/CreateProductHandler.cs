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

        // Currency snapshots from the account at creation (spec §16).
        var account = await accounts.GetCurrentAsync(ct);
        if (account is null)
            return Result.Failure<Guid>(Error.NotFound("Account", accountId));

        var product = Product.Create(
            Guid.NewGuid(), accountId, SeedDefaults.FarmId,
            command.Name,
            Enum.Parse<ProductType>(command.ProductType, ignoreCase: true),
            Enum.Parse<ProductUnit>(command.DefaultUnit, ignoreCase: true),
            command.DefaultPriceMinorUnits,
            account.DefaultCurrencyCode, account.DefaultCurrencyMinorUnit,
            command.Notes);

        await products.AddAsync(product, ct);
        await products.AddMappingAsync(
            ProductEggGradeMapping.Create(Guid.NewGuid(), accountId, product.Id, grade.Id), ct);

        await audit.WriteAsync("Product.Create", nameof(Product), product.Id,
            details: new { product.Name, product.ProductType, EggGrade = grade.Name }, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(product.Id);
    }
}
