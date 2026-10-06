namespace Cluckwork.Domain.Common.Architecture;

[ModuleOwner("Commerce", "module",
    Namespaces = [
        "Cluckwork.Domain.Modules.Commerce",
        "Cluckwork.Application.Modules.Commerce",
        "Cluckwork.Infrastructure.Modules.Commerce",
    ],
    Implementations = [
        "Cluckwork.Infrastructure.Modules.Commerce.Repositories.CustomerRepository",
        "Cluckwork.Infrastructure.Modules.Commerce.Repositories.CommerceFixture",
        "Cluckwork.Infrastructure.Modules.Commerce.Repositories.EggUnitConversionRepository",
        "Cluckwork.Infrastructure.Modules.Commerce.Repositories.PaymentRepository",
        "Cluckwork.Infrastructure.Modules.Commerce.Repositories.ProductRepository",
        "Cluckwork.Infrastructure.Modules.Commerce.Repositories.SalesOrderAllocationRepository",
        "Cluckwork.Infrastructure.Modules.Commerce.Repositories.SalesOrderRepository",
    ])]
[ModuleEdge(
    "Commerce", "Access", "R",
    "ConfirmSaleHandler reads through Access's IAccessLookup port (GetEffectiveRoleAsync and GetAssignedFlocksAsync, #857) inside the confirm transaction, so a plain Worker's committed flock assignments and the discount ceiling bound to their effective role are read at the moment of confirmation rather than from a request-start snapshot (#727). Design 3.4 shows Commerce -> Access as none; this is live coupling the target design has still to remove.",
    "Cluckwork.Application.Modules.Commerce.Sales.ConfirmSale.ConfirmSaleHandler")]
[ModuleEdge(
    "Commerce", "EggOperations", "W",
    "FIFO egg stock. ConfirmSaleHandler and VoidSaleHandler lock, draw from and return egg lots through Egg Operations' IEggStock port, which adds the lot changes and Sale or Void movements to the confirm and void transactions and never saves (#854); Commerce sees plans and lot ids, never Domain.Eggs.EggLot. Products, order lines and confirm refusals read grades through IEggGradeLookup in CreateProductHandler, UpdateProductHandler, AddOrderItemHandler and ConfirmSaleHandler. Design 3.4 row Commerce -> Egg Ops = W.",
    "Cluckwork.Application.Modules.Commerce.Catalog.CreateProduct.CreateProductHandler",
    "Cluckwork.Application.Modules.Commerce.Catalog.UpdateProduct.UpdateProductHandler",
    "Cluckwork.Application.Modules.Commerce.Sales.AddOrderItem.AddOrderItemHandler",
    "Cluckwork.Application.Modules.Commerce.Sales.ConfirmSale.ConfirmSaleHandler",
    "Cluckwork.Application.Modules.Commerce.Sales.VoidSale.VoidSaleHandler")]
[ModuleEdge(
    "Commerce", "Farm", "R",
    "Orders and products snapshot the farm's currency through Farm's IAccountRepository (CreateSalesOrderHandler, CreateProductHandler, UpdateProductHandler), with the sales-order path holding the FOR SHARE lock on the account row (#162) so an order cannot land in a denomination the farm is mid-flight out of; ConfirmSaleHandler also reads the farm's discount ceiling and worker sale-allocation policy off Account. Design 3.4 row Commerce -> Farm = R.",
    "Cluckwork.Application.Modules.Commerce.Catalog.CreateProduct.CreateProductHandler",
    "Cluckwork.Application.Modules.Commerce.Catalog.UpdateProduct.UpdateProductHandler",
    "Cluckwork.Application.Modules.Commerce.Sales.ConfirmSale.ConfirmSaleHandler",
    "Cluckwork.Application.Modules.Commerce.Sales.CreateSalesOrder.CreateSalesOrderHandler")]
internal static class CommerceModuleRules { }
