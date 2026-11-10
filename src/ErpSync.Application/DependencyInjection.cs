using ErpSync.Application.Orders;
using ErpSync.Application.Sync;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace ErpSync.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddOptions<SyncOptions>().BindConfiguration(SyncOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<OrderOptions>().BindConfiguration(OrderOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SyncRequestChannel>();
        services.AddSingleton<OrderDispatchSignal>();

        services.AddScoped<IEntitySynchronizer, ProductSynchronizer>();
        services.AddScoped<IEntitySynchronizer, StockSynchronizer>();
        services.AddScoped<IEntitySynchronizer, CustomerSynchronizer>();
        services.AddScoped<ISyncRunner, SyncRunner>();

        services.AddScoped<CreateSalesOrderHandler>();
        services.AddScoped<IOrderDispatcher, OrderDispatcher>();
        services.AddValidatorsFromAssemblyContaining<CreateSalesOrderValidator>(includeInternalTypes: true);

        return services;
    }
}
