using Amazon.Runtime;
using Amazon.S3;
using Azure.Storage.Blobs;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.DataExchange;
using Base.Application.Documents.Generation;
using Base.Application.Files;
using Base.Application.Messaging;
using Base.Application.Notifications;
using Base.Application.Operations;
using Base.Application.Subscriptions;
using Base.Application.Tenancy;
using Base.Application.Users;
using Base.Infrastructure.Auditing;
using Base.Infrastructure.DataExchange;
using Base.Infrastructure.Documents;
using Base.Infrastructure.Files;
using Base.Infrastructure.Health;
using Base.Infrastructure.Identity;
using Base.Infrastructure.Jobs;
using Base.Infrastructure.Notifications;
using Base.Infrastructure.Notifications.Providers;
using Base.Infrastructure.Operations;
using Base.Infrastructure.Outbox;
using Base.Infrastructure.Persistence;
using Base.Infrastructure.Persistence.Interceptors;
using Base.Infrastructure.Startup;
using Base.Infrastructure.Tenancy;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Default";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ICurrentUser, SystemCurrentUser>();
        AddRunAs(services);
        services.AddHybridCache();

        services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName);
        services.AddOptions<SeedOptions>().BindConfiguration(SeedOptions.SectionName);
        services.AddOptions<TenancyOptions>().BindConfiguration(TenancyOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<OutboxOptions>().BindConfiguration(OutboxOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();

        var connectionString = ResolveConnectionString(configuration, environment);
        AddPersistence(services, connectionString, environment);
        AddIdentityCore(services);
        AddFileStorage(services, configuration, environment);
        AddNotifications(services);
        AddDocuments(services, configuration);
        AddDataExchange(services);
        AddOperations(services, configuration);
        AddHangfire(services, configuration, connectionString);

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ITenantResolver, TenantResolver>();
        services.AddScoped<IPlatformTenant, PlatformTenant>();
        services.AddScoped<ITenantUsageReader, TenantUsageReader>();
        services.AddScoped<ITenantAdministration, TenantAdministrationService>();

        services.AddScoped<DomainEventDispatcher>();
        if (configuration.GetValue(OutboxOptions.SectionName + ":" + nameof(OutboxOptions.Enabled), true))
        {
            services.AddHostedService<OutboxProcessor>();
        }

        services.AddSingleton<AuditedEntityTypes>();

        services.AddScoped<IStartupTask, MigrateDatabaseTask>();
        services.AddScoped<IStartupTask, DefaultTenantSeedTask>();
        services.AddScoped<IStartupTask, SettingsSeedTask>();
        services.AddScoped<IStartupTask, IdentitySeedTask>();
        services.AddScoped<IStartupTask, SamplePlansSeedTask>();

        AddHealthChecks(services, configuration);

        return services;
    }

    /// <summary>
    /// The configured connection string. In Development only, an empty one falls back to
    /// <see cref="DevelopmentDatabase"/> (LocalDB on Windows, docker-compose SQL Server elsewhere).
    /// </summary>
    internal static string ResolveConnectionString(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration.GetConnectionString(ConnectionStringName);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return environment.IsDevelopment()
            ? DevelopmentDatabase.Default
            : throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is missing. Set the ConnectionStrings__{ConnectionStringName} environment variable, or for local work: dotnet user-secrets set \"ConnectionStrings:{ConnectionStringName}\" \"...\"");
    }

    private static void AddPersistence(IServiceCollection services, string connectionString, IHostEnvironment environment)
    {
        services.AddScoped<AuditingInterceptor>();
        services.AddScoped<DomainEventOutboxInterceptor>();
        services.AddScoped<AuditTrailInterceptor>();
        services.AddSingleton<SlowQueryInterceptor>();

        services.AddDbContext<AppDbContext>((provider, options) =>
        {
            var database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                sql.EnableRetryOnFailure(database.MaxRetryCount);
                sql.CommandTimeout(database.CommandTimeoutSeconds);
            });
            options.AddInterceptors(
                provider.GetRequiredService<DomainEventOutboxInterceptor>(),
                provider.GetRequiredService<AuditingInterceptor>(),
                provider.GetRequiredService<AuditTrailInterceptor>(), // last of the save interceptors: sees final ids, stamps and tenants
                provider.GetRequiredService<SlowQueryInterceptor>());

            if (environment.IsDevelopment())
            {
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            }
        });

        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddDataProtection()
            .SetApplicationName("Base")
            .PersistKeysToDbContext<AppDbContext>();
    }

    private static void AddIdentityCore(IServiceCollection services)
    {
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = false; // uniqueness is per tenant (database index)
                // NIST SP 800-63B: length over composition rules, so passphrases work.
                options.Password.RequiredLength = 12;
                options.Password.RequiredUniqueChars = 4;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddTokenProvider<InviteTokenProvider>(InviteTokenProvider.ProviderName)
            .AddClaimsPrincipalFactory<AppUserClaimsPrincipalFactory>();

        // Password-reset and similar tokens: hashed by the token provider, short-lived.
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(1));

        services.AddScoped<IdentityQueries>();
        services.AddScoped<IUserAdministration, UserAdministrationService>();
        services.AddScoped<IRoleAdministration, RoleAdministrationService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IdentityEmailHandlers>();
        services.AddScoped<IDomainEventHandler<UserInvited>>(sp => sp.GetRequiredService<IdentityEmailHandlers>());
        services.AddScoped<IDomainEventHandler<PasswordResetRequested>>(sp => sp.GetRequiredService<IdentityEmailHandlers>());
    }

    private static void AddFileStorage(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection(FileStorageOptions.SectionName).Get<FileStorageOptions>() ?? new FileStorageOptions();

        if (options.S3 is { Bucket.Length: > 0 } s3)
        {
            services.AddSingleton<IAmazonS3>(_ =>
            {
                var config = new AmazonS3Config { ForcePathStyle = s3.ForcePathStyle, AuthenticationRegion = s3.Region };
                if (!string.IsNullOrEmpty(s3.ServiceUrl))
                {
                    config.ServiceURL = s3.ServiceUrl;
                }

                return string.IsNullOrEmpty(s3.AccessKey)
                    ? new AmazonS3Client(config)
                    : new AmazonS3Client(new BasicAWSCredentials(s3.AccessKey, s3.SecretKey), config);
            });
            services.AddSingleton<IFileStorageProvider>(sp =>
                new S3FileStorageProvider(sp.GetRequiredService<IAmazonS3>(), s3.Bucket, sp.GetRequiredService<TimeProvider>()));
        }

        if (options.AzureBlob is { ConnectionString.Length: > 0 } azure)
        {
            services.AddSingleton<IFileStorageProvider>(sp =>
                new AzureBlobFileStorageProvider(new BlobContainerClient(azure.ConnectionString, azure.Container), sp.GetRequiredService<TimeProvider>()));
        }

        // LocalDisk: whenever configured, and always in Development so the app runs with zero setup.
        if (options.LocalDisk is not null || environment.IsDevelopment())
        {
            var root = options.LocalDisk?.RootPath ?? new FileStorageOptions.LocalDiskOptions().RootPath;
            var fullRoot = Path.IsPathRooted(root) ? root : Path.Join(environment.ContentRootPath, root);
            services.AddSingleton<IFileStorageProvider>(new LocalDiskFileStorageProvider(fullRoot));
        }


        if (!string.IsNullOrWhiteSpace(configuration[ClamAvOptions.SectionName + ":" + nameof(ClamAvOptions.Host)]))
        {
            services.AddOptions<ClamAvOptions>().BindConfiguration(ClamAvOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
            services.AddScoped<IFileUploadCheck, ClamAvUploadCheck>();
        }

        // Always available, used only for the categories Files.CategoryProviders sends to it (capped by settings).
        services.AddSingleton<IFileStorageProvider, DatabaseFileStorageProvider>();
        services.AddSingleton<IFileStorageProviderResolver, FileStorageProviderResolver>();
    }

    private static void AddNotifications(IServiceCollection services)
    {
        services.AddOptions<NotificationOptions>().BindConfiguration(NotificationOptions.SectionName);
        services.AddHttpClient(HttpNotificationProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<NotificationProviderRegistry>();
        services.AddScoped<INotificationChannels, NotificationChannels>();
        services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();
        services.AddScoped<IEmailSender, NotificationEmailSender>();
        services.AddScoped<IStartupTask, NotificationConfigurationCheck>();
    }

    /// <summary>
    /// Wraps the host's <see cref="ICurrentUser"/> (registered before this runs) so background work can act as a user
    /// through <see cref="IRunAsUser"/>.
    /// </summary>
    private static void AddRunAs(IServiceCollection services)
    {
        var inner = services.Last(d => d.ServiceType == typeof(ICurrentUser));
        services.Remove(inner);
        services.AddScoped<CurrentUserOverride>();
        services.AddScoped<ICurrentUser>(sp => new OverridableCurrentUser(sp.GetRequiredService<CurrentUserOverride>(), Resolve(sp, inner)));
        services.AddScoped<IRunAsUser, RunAsUser>();

        static ICurrentUser Resolve(IServiceProvider sp, ServiceDescriptor descriptor) =>
            descriptor.ImplementationInstance as ICurrentUser
            ?? descriptor.ImplementationFactory?.Invoke(sp) as ICurrentUser
            ?? (ICurrentUser)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
    }

    /// <summary>
    /// The database gates readiness (<c>/health/ready</c>). Everything else is a <see cref="HealthTags.Dependency"/>:
    /// shown on the system page, but the app keeps serving when one is down.
    /// </summary>
    private static void AddHealthChecks(IServiceCollection services, IConfiguration configuration)
    {
        var timeout = TimeSpan.FromSeconds(10);
        string[] dependency = [HealthTags.Dependency];
        var checks = services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database", tags: [HealthTags.Ready])
            .AddCheck<FileStorageHealthCheck>("file-storage", tags: dependency, timeout: timeout)
            .AddCheck<DatabaseFileQuotaHealthCheck>("database-file-quota", tags: dependency, timeout: timeout)
            .AddCheck<NotificationProvidersHealthCheck>("notification-providers", tags: dependency, timeout: timeout)
            .AddCheck<OutboxHealthCheck>("outbox", tags: dependency, timeout: timeout)
            .AddCheck<ScheduledJobsHealthCheck>("scheduled-jobs", tags: dependency, timeout: timeout);

        if (configuration.GetSection(GotenbergOptions.SectionName).Get<GotenbergOptions>()?.BaseUrl is not null)
        {
            checks.AddCheck<GotenbergHealthCheck>("gotenberg", tags: dependency, timeout: timeout);
        }

        if (!string.IsNullOrWhiteSpace(configuration[ClamAvOptions.SectionName + ":" + nameof(ClamAvOptions.Host)]))
        {
            checks.AddCheck<ClamAvHealthCheck>("clamav", tags: dependency, timeout: timeout);
        }

        if ((configuration.GetSection(HangfireOptions.SectionName).Get<HangfireOptions>() ?? new HangfireOptions()).Enabled)
        {
            checks.AddCheck<HangfireHealthCheck>("hangfire", tags: dependency, timeout: timeout);
        }
    }

    private static void AddOperations(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JobSchedulerOptions>().BindConfiguration(JobSchedulerOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<JobSignal>();
        services.AddScoped<IJobScheduler, JobSchedulerStatus>();
        if (configuration.GetValue(JobSchedulerOptions.SectionName + ":" + nameof(JobSchedulerOptions.Enabled), true))
        {
            services.AddHostedService<JobSchedulerService>();
        }

        services.AddScoped<IScheduledJob, AuditRetentionJob>();
        services.AddScoped<IScheduledJob, NotificationRetentionJob>();
        services.AddScoped<IScheduledJob, FileRetentionJob>();
        services.AddScoped<IScheduledJob, OutboxCleanupJob>();
        services.AddScoped<IScheduledJob, ErrorLogRetentionJob>();
        services.AddScoped<IErrorLog, ErrorLog>();
        services.AddScoped<ISystemStatus, SystemStatus>();
    }

    private static void AddDataExchange(IServiceCollection services)
    {
        services.AddSingleton<ITabularWriter, CsvWriter>();
        services.AddSingleton<ITabularWriter, XlsxWriter>();
        services.AddSingleton<ITabularReader, CsvReader>();
        services.AddSingleton<ITabularReader, XlsxReader>();
    }

    private static void AddDocuments(IServiceCollection services, IConfiguration configuration)
    {

        var gotenberg = configuration.GetSection(GotenbergOptions.SectionName).Get<GotenbergOptions>();
        if (gotenberg?.BaseUrl is { } baseUrl)
        {
            services.AddOptions<GotenbergOptions>().BindConfiguration(GotenbergOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
            services.AddHttpClient(GotenbergPdfRenderer.HttpClientName, client =>
            {
                client.BaseAddress = baseUrl;
                client.Timeout = TimeSpan.FromSeconds(gotenberg.TimeoutSeconds);
            });
            services.AddSingleton<IDocumentRenderer, GotenbergPdfRenderer>();
        }
    }

    private static void AddHangfire(IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        var options = configuration.GetSection(HangfireOptions.SectionName).Get<HangfireOptions>() ?? new HangfireOptions();
        services.AddSingleton(Options.Create(options));
        if (!options.Enabled)
        {
            return;
        }

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
            {
                SchemaName = "hangfire",
                PrepareSchemaIfNecessary = true,
                QueuePollInterval = TimeSpan.FromSeconds(15),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                DisableGlobalLocks = true,
            }));

        services.AddHangfireServer(server =>
        {
            server.ServerName = options.ServerName;
            server.Queues = [.. options.Queues];
            if (options.WorkerCount > 0)
            {
                server.WorkerCount = options.WorkerCount;
            }
        });
    }
}
