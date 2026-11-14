using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zladovce.Application.Audit;
using Zladovce.Application.Cart;
using Zladovce.Application.Catalog;
using Zladovce.Application.Categories;
using Zladovce.Application.Common;
using Zladovce.Application.Inventory;
using Zladovce.Application.Media;
using Zladovce.Application.Identity;
using Zladovce.Application.Notifications;
using Zladovce.Application.Orders;
using Zladovce.Application.Seo;
using Zladovce.Application.Shipping;
using Zladovce.Application.StoreSettings;
using Zladovce.Application.Tracking;

namespace Zladovce.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddScoped<ICurrentAdminResolver, CurrentAdminResolver>();
        services.AddScoped<IStoreSettingsService, StoreSettingsService>();

        services.AddSingleton<IHtmlContentSanitizer, HtmlContentSanitizer>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IAdminProductService, AdminProductService>();
        services.AddScoped<IPublicCatalogService, PublicCatalogService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IMediaUploadService, MediaUploadService>();
        services.AddScoped<ICartPricingService, CartPricingService>();
        services.AddScoped<ISeoRoutesService, SeoRoutesService>();
        services.AddScoped<ICheckoutService, CheckoutService>();
        services.AddScoped<IOrderAdminService, OrderAdminService>();
        services.AddScoped<ICourierService, CourierService>();
        services.AddScoped<IEmailOutbox, EmailOutbox>();
        services.AddScoped<IOrderNotifier, OrderNotifier>();
        services.AddScoped<ITrackingService, TrackingService>();
        services.AddScoped<IAdminEmailService, AdminEmailService>();
        services.AddImageProcessing();

        // Every request DTO validator in this assembly; the API runs them automatically.
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        return services;
    }

    /// <summary>
    /// The image processor alone: all the image Lambda needs from this project. It requires an
    /// <see cref="IMediaStorage"/> and an <see cref="IImageTransformer"/> from the infrastructure, and no database.
    /// </summary>
    public static IServiceCollection AddImageProcessing(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IImageProcessor, ImageProcessor>();
        return services;
    }
}
