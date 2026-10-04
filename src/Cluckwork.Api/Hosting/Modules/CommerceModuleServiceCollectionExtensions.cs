using Cluckwork.Application.Features.Customers;
using Cluckwork.Application.Features.Customers.CreateCustomer;
using Cluckwork.Application.Features.Customers.UpdateCustomer;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Application.Features.Sales.AddOrderItem;
using Cluckwork.Application.Features.Sales.CancelSalesOrder;
using Cluckwork.Application.Features.Sales.ConfirmSale;
using Cluckwork.Application.Features.Sales.CreateSalesOrder;
using Cluckwork.Application.Features.Sales.RecordPayment;
using Cluckwork.Application.Features.Sales.RemoveOrderItem;
using Cluckwork.Application.Features.Sales.UpdateOrderItem;
using Cluckwork.Application.Features.Sales.VoidPayment;
using Cluckwork.Application.Features.Sales.VoidSale;
using Cluckwork.Infrastructure.Repositories;
using FluentValidation;

namespace Cluckwork.Api.Hosting.Modules;

internal static class CommerceModuleServiceCollectionExtensions
{
    public static IServiceCollection AddCommerceModule(this IServiceCollection services)
    {
        services.AddScoped<
            Cluckwork.Application.Features.Catalog.IProductRepository,
            ProductRepository>();
        services.AddScoped<
            Cluckwork.Application.Features.Catalog.IEggUnitConversionRepository,
            EggUnitConversionRepository>();
        services.AddScoped<ISalesOrderRepository, SalesOrderRepository>();
        services.AddScoped<
            ISalesOrderAllocationRepository,
            SalesOrderAllocationRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Features.Catalog.CreateProduct.CreateProductCommand>,
            Cluckwork.Application.Features.Catalog.CreateProduct.CreateProductValidator>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Features.Catalog.UpdateProduct.UpdateProductCommand>,
            Cluckwork.Application.Features.Catalog.UpdateProduct.UpdateProductValidator>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Features.Catalog.UpdateEggUnitConversion.UpdateEggUnitConversionCommand>,
            Cluckwork.Application.Features.Catalog.UpdateEggUnitConversion.UpdateEggUnitConversionValidator>();
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
            Cluckwork.Application.Features.Catalog.IEggUnitConversionLookup,
            Cluckwork.Application.Features.Catalog.EggUnitConversionLookup>();
        services.AddScoped<VoidPaymentHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Catalog.CreateProduct.CreateProductHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Catalog.UpdateProduct.UpdateProductHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Catalog.SetProductActive.SetProductActiveHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Catalog.UpdateEggUnitConversion.UpdateEggUnitConversionHandler>();

        return services;
    }
}
