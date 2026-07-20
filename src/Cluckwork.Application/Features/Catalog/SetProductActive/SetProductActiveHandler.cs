namespace Cluckwork.Application.Features.Catalog.SetProductActive;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;

public sealed class SetProductActiveHandler(
    IProductRepository products,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result> HandleAsync(Guid productId, bool active, CancellationToken ct)
    {
        var product = await products.GetByIdAsync(productId, ct);
        if (product is null)
            return Result.Failure(Error.NotFound("Product", productId));

        var result = active ? product.Activate() : product.Deactivate();
        if (result.IsFailure) return result;

        await audit.WriteAsync(
            active ? "Product.Activate" : "Product.Deactivate",
            nameof(Product), product.Id,
            details: new { product.Name }, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
