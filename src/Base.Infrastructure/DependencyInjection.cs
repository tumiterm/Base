using Amazon.Runtime;
using Amazon.S3;
using Azure.Storage.Blobs;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Files;
using Base.Application.Messaging;
using Base.Application.Users;
using Base.Infrastructure.Email;
using Base.Infrastructure.Files;
using Base.Infrastructure.Identity;
using Base.Infrastructure.Jobs;
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
        services.AddHybridCache();

        services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName);
        services.AddOptions<SeedOptions>().BindConfiguration(SeedOptions.SectionName);
        services.AddOptions<TenancyOptions>().BindConfiguration(TenancyOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<OutboxOptions>().BindConfiguration(OutboxOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();

        AddPersistence(services, configuration, environment);
        AddIdentityCore(services);
        AddFileStorage(services, configuration, environment);
        AddEmail(services, configuration);
        AddHangfire(services, configuration);

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ITenantResolver, TenantResolver>();

        services.AddScoped<DomainEventDispatcher>();
        if (configuration.GetValue(OutboxOptions.SectionName + ":" + nameof(OutboxOptions.Enabled), true))
        {
            services.AddHostedService<OutboxProcessor>();
        }

        services.AddScoped<IStartupTask, MigrateDatabaseTask>();
        services.AddScoped<IStartupTask, DefaultTenantSeedTask>();
        services.AddScoped<IStartupTask, SettingsSeedTask>();
        services.AddScoped<IStartupTask, IdentitySeedTask>();

        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

        return services;
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is missing. Set it with: dotnet user-secrets set \"ConnectionStrings:{ConnectionStringName}\" \"...\"");

        services.AddScoped<AuditingInterceptor>();
        services.AddScoped<DomainEventOutboxInterceptor>();

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
                provider.GetRequiredService<AuditingInterceptor>());

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
            var fullRoot = Path.IsPathRooted(root) ? root : Path.Combine(environment.ContentRootPath, root);
            services.AddSingleton<IFileStorageProvider>(new LocalDiskFileStorageProvider(fullRoot));
        }

        services.AddSingleton<IFileStorageProviderResolver, FileStorageProviderResolver>();
    }

    private static void AddEmail(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SmtpOptions>().BindConfiguration(SmtpOptions.SectionName);
        if (string.IsNullOrWhiteSpace(configuration[$"{SmtpOptions.SectionName}:{nameof(SmtpOptions.Host)}"]))
        {
            services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, MailKitEmailSender>();
        }
    }

    private static void AddHangfire(IServiceCollection services, IConfiguration configuration)
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
            .UseSqlServerStorage(configuration.GetConnectionString(ConnectionStringName), new SqlServerStorageOptions
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
