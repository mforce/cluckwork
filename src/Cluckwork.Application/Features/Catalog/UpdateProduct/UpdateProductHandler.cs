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

        // Needed only for the unpriced → priced transition, but the account is
        // a single filtered row and this path is not hot.
        var account = await accounts.GetCurrentAsync(ct);
        if (account is null)
            return Result.Failure(Error.NotFound("Account", "current"));

        var result = product.Update(
            command.Name,
            Enum.Parse<ProductUnit>(command.DefaultUnit, ignoreCase: true),
            command.DefaultPriceMinorUnits,
            command.Notes,
            account.DefaultCurrencyCode,
            account.DefaultCurrencyMinorUnit);
        if (result.IsFailure) return result;

        // Re-pointing the grade is safe: sold lines snapshot at sale time
        // (part 2), so history never reinterprets.
        var mapping = await products.GetMappingAsync(product.Id, ct);
        if (mapping is null)
            return Result.Failure(Error.NotFound("ProductEggGradeMapping", product.Id));
        mapping.Repoint(grade.Id);

        await audit.WriteAsync("Product.Update", nameof(Product), product.Id,
            details: new { product.Name, product.DefaultUnit, product.DefaultPriceMinorUnits, EggGrade = grade.Name },
            ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
