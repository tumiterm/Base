using Base.Application.Auditing;
using Base.Application.Branding;
using Base.Application.Common.Records;
using Base.Application.Communications;
using Base.Application.DataExchange;
using Base.Application.Documents;
using Base.Application.Documents.Generation;
using Base.Application.Files;
using Base.Application.Flags;
using Base.Application.Integrations;
using Base.Application.MasterData;
using Base.Application.Messaging;
using Base.Application.Notifications;
using Base.Application.Operations;
using Base.Application.Payments;
using Base.Application.People;
using Base.Application.Releases;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Application.Users;
using Base.Domain.Communications;
using Base.Domain.DataExchange;
using Base.Domain.Integrations;
using Base.Domain.Notifications;
using Base.Domain.Payments;
using Base.Domain.Subscriptions;
using Base.Domain.Tenancy;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Base.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        services.AddScoped<IApplicationSettingService, ApplicationSettingService>();
        services.AddScoped<IEmailDeliveryTest, EmailDeliveryTest>();
        services.AddScoped<FileService>();
        services.AddScoped<IFileService>(sp => sp.GetRequiredService<FileService>());
        services.AddScoped<IAuditTrail, AuditTrailService>();

        services.AddScoped<IBrandingService, BrandingService>();

        services.AddScoped<LookupService>();
        services.AddScoped<ILookupService>(sp => sp.GetRequiredService<LookupService>());
        services.AddScoped<ILookupDefaults>(sp => sp.GetRequiredService<LookupService>());
        services.AddScoped<IDomainEventHandler<TenantCreated>, LookupDefaultsForNewTenants>();

        services.AddScoped<IApiClientAdministration, ApiClientAdministration>();
        services.AddScoped<IApiKeyAuthenticator, ApiKeyAuthenticator>();
        services.AddScoped<IScheduledJob, ApiKeyExpiryJob>();
        services.AddScoped<IDomainEventHandler<ApiKeyExpiring>, ApiKeyExpiryNotice>();

        services.AddScoped<IRecordLabels, RecordLabels>();

        services.AddScoped<IPersonService, PersonService>();
        services.AddScoped<IRecordLabelProvider, PersonRecordLabels>();
        services.AddScoped<IExporter, PeopleExporter>();
        services.AddScoped<IImporter, PeopleImporter>();

        services.AddScoped<ITenantEntitlements, TenantEntitlements>();
        services.TryAddSingleton<IReleaseCatalog, DefaultReleaseCatalog>();
        services.AddScoped<ITenantReleases, TenantReleases>();
        services.AddScoped<IScheduledJob, ReleaseWindowJob>();
        services.AddScoped<IPlanAdministration, PlanAdministration>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        services.AddScoped<IScheduledJob, SubscriptionLifecycleJob>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IDomainEventHandler<PaymentSucceeded>, PaymentSucceededHandler>();
        services.AddScoped<RecurringPayments>();
        services.AddScoped<RecurringPaymentLifecycle>();
        services.AddScoped<IDomainEventHandler<RecurringPaymentActivated>>(sp => sp.GetRequiredService<RecurringPaymentLifecycle>());
        services.AddScoped<IDomainEventHandler<PaymentSucceeded>>(sp => sp.GetRequiredService<RecurringPaymentLifecycle>());
        services.AddScoped<IDomainEventHandler<SubscriptionStatusChanged>>(sp => sp.GetRequiredService<RecurringPaymentLifecycle>());
        services.AddScoped<IDomainEventHandler<RecurringPaymentStoppedByGateway>>(sp => sp.GetRequiredService<RecurringPaymentLifecycle>());
        services.AddScoped<IScheduledJob, PaymentExpiryJob>();
        services.AddScoped<SubscriptionNotices>();
        services.AddScoped<IDomainEventHandler<SubscriptionStatusChanged>>(sp => sp.GetRequiredService<SubscriptionNotices>());
        services.AddScoped<IDomainEventHandler<SubscriptionTrialEnding>>(sp => sp.GetRequiredService<SubscriptionNotices>());
        services.AddScoped<ExportService>();
        services.AddScoped<IExportService>(sp => sp.GetRequiredService<ExportService>());
        services.AddScoped<ImportService>();
        services.AddScoped<IImportService>(sp => sp.GetRequiredService<ImportService>());
        services.AddScoped<IDomainEventHandler<ExportQueued>, ExportQueuedHandler>();
        services.AddScoped<IDomainEventHandler<ImportQueued>, ImportQueuedHandler>();
        services.AddScoped<IExporter, UsersExporter>();
        services.AddScoped<IExporter, AuditExporter>();
        services.AddScoped<IExporter, NotificationLogExporter>();
        services.AddScoped<IExporter, DocumentsExporter>();
        services.AddScoped<IImporter, UserInviteImporter>();

        services.AddScoped<DocumentService>();
        services.AddScoped<IDocumentService>(sp => sp.GetRequiredService<DocumentService>());
        services.AddScoped<IRecordLabelProvider, DocumentRecordLabels>();
        services.AddScoped<IDocumentRetention, DocumentRetention>();
        services.AddScoped<IScheduledJob, DocumentRetentionJob>();
        services.AddScoped<IDocumentGenerator, DocumentGenerator>();
        services.AddSingleton<IDocumentRenderer, HtmlDocumentRenderer>();
        services.TryAddScoped<IDocumentAccessPolicy, DefaultDocumentAccessPolicy>();

        services.AddScoped<NotificationComposer>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IDomainEventHandler<NotificationQueued>, NotificationDispatcher>();
        services.AddScoped<INotificationInbox, NotificationInbox>();
        services.AddScoped<INotificationLog, NotificationLog>();
        services.AddScoped<INotificationPreferences, NotificationPreferences>();
        services.AddScoped<INotificationTemplateAdmin, NotificationTemplateAdmin>();

        services.TryAddSingleton<IFeatureFlagCatalog, DefaultFeatureFlagCatalog>();
        services.AddScoped<IFeatureFlags, FeatureFlags>();

        services.AddScoped<AudienceResolver>();
        services.AddScoped<IAudienceSource, PersonAudience>();
        services.AddScoped<IAudienceSource, UserAudience>();
        services.AddScoped<IMessagingService, MessagingService>();
        services.AddScoped<IMessageTemplateService, MessageTemplateService>();
        services.AddScoped<BroadcastSender>();
        services.AddScoped<IDomainEventHandler<BroadcastDue>, BroadcastDueHandler>();
        services.AddScoped<IScheduledJob, ScheduledMessagesJob>();
        services.TryAddScoped<IStoredFileAccessPolicy, DefaultStoredFileAccessPolicy>();

        return services;
    }
}
