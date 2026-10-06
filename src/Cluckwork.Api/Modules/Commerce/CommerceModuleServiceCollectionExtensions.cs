using Cluckwork.Application.Modules.Commerce.Contracts;
using Cluckwork.Application.Modules.Commerce.Customers;
using Cluckwork.Application.Modules.Commerce.Customers.CreateCustomer;
using Cluckwork.Application.Modules.Commerce.Customers.UpdateCustomer;
using Cluckwork.Application.Modules.Commerce.Sales;
using Cluckwork.Application.Modules.Commerce.Sales.AddOrderItem;
using Cluckwork.Application.Modules.Commerce.Sales.CancelSalesOrder;
using Cluckwork.Application.Modules.Commerce.Sales.ConfirmSale;
using Cluckwork.Application.Modules.Commerce.Sales.CreateSalesOrder;
using Cluckwork.Application.Modules.Commerce.Sales.RecordPayment;
using Cluckwork.Application.Modules.Commerce.Sales.RemoveOrderItem;
using Cluckwork.Application.Modules.Commerce.Sales.UpdateOrderItem;
using Cluckwork.Application.Modules.Commerce.Sales.VoidPayment;
using Cluckwork.Application.Modules.Commerce.Sales.VoidSale;
using Cluckwork.Infrastructure.Modules.Commerce.Repositories;
using FluentValidation;

namespace Cluckwork.Api.Modules.Commerce;

internal static class CommerceModuleServiceCollectionExtensions
{
    public static IServiceCollection AddCommerceModule(this IServiceCollection services)
    {
        services.AddScoped<
            Cluckwork.Application.Modules.Commerce.Catalog.IProductRepository,
            ProductRepository>();
        services.AddScoped<
            Cluckwork.Application.Modules.Commerce.Catalog.IEggUnitConversionRepository,
            EggUnitConversionRepository>();
        services.AddScoped<ISalesOrderRepository, SalesOrderRepository>();
        services.AddScoped<
            ISalesOrderAllocationRepository,
            SalesOrderAllocationRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Modules.Commerce.Contracts.CreateProductCommand>,
            Cluckwork.Application.Modules.Commerce.Catalog.CreateProduct.CreateProductValidator>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Modules.Commerce.Contracts.UpdateProductCommand>,
            Cluckwork.Application.Modules.Commerce.Catalog.UpdateProduct.UpdateProductValidator>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Modules.Commerce.Contracts.UpdateEggUnitConversionCommand>,
            Cluckwork.Application.Modules.Commerce.Catalog.UpdateEggUnitConversion.UpdateEggUnitConversionValidator>();
        services.AddScoped<
            IValidator<CreateCustomerCommand>,
            CreateCustomerValidator>();
        services.AddScoped<
            IValidator<UpdateCustomerCommand>,
            UpdateCustomerValidator>();
        services.AddScoped<
            IValidator<CreateSalesOrderCommand>,
            CreateSalesOrderValidator>();
        services.AddScoped<
            IValidator<AddOrderItemCommand>,
            AddOrderItemValidator>();
        services.AddScoped<
            IValidator<UpdateOrderItemCommand>,
            UpdateOrderItemValidator>();
        services.AddScoped<IValidator<VoidSaleCommand>, VoidSaleValidator>();
        services.AddScoped<IValidator<ConfirmSaleCommand>, ConfirmSaleValidator>();
        services.AddScoped<
            IValidator<RecordPaymentCommand>,
            RecordPaymentValidator>();
        services.AddScoped<
            IValidator<VoidPaymentCommand>,
            VoidPaymentValidator>();
        services.AddScoped<CreateCustomerHandler>();
        services.AddScoped<UpdateCustomerHandler>();
        services.AddScoped<CreateSalesOrderHandler>();
        services.AddScoped<AddOrderItemHandler>();
        services.AddScoped<CancelSalesOrderHandler>();
        services.AddScoped<RemoveOrderItemHandler>();
        services.AddScoped<UpdateOrderItemHandler>();
        services.AddScoped<ConfirmSaleHandler>();
        services.AddScoped<VoidSaleHandler>();
        services.AddScoped<RecordPaymentHandler>();
        services.AddScoped<
            ICommerceModule,
            CommerceModule>();
        services.AddScoped<
            Cluckwork.Application.Modules.Commerce.Contracts.IEggUnitConversionLookup,
            Cluckwork.Application.Modules.Commerce.Catalog.EggUnitConversionLookup>();
        services.AddScoped<VoidPaymentHandler>();
        services.AddScoped<
            Cluckwork.Application.Modules.Commerce.Catalog.CreateProduct.CreateProductHandler>();
        services.AddScoped<
            Cluckwork.Application.Modules.Commerce.Catalog.UpdateProduct.UpdateProductHandler>();
        services.AddScoped<
            Cluckwork.Application.Modules.Commerce.Catalog.SetProductActive.SetProductActiveHandler>();
        services.AddScoped<
            Cluckwork.Application.Modules.Commerce.Catalog.UpdateEggUnitConversion.UpdateEggUnitConversionHandler>();

        return services;
    }
}
