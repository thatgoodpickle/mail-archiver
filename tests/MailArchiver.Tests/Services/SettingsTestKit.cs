using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace MailArchiver.Tests.Services;

/// <summary>A small, real settings stack (options, snapshot, overlays, service) over the test database, with a recording audit chain and access log.</summary>
internal sealed class SettingsHarness
{
    public ServiceProvider Sp { get; init; } = null!;
    public RecordingChain Chain { get; init; } = null!;
    public RecordingAccessLog Log { get; init; } = null!;
    public Mock<IUserService> Users { get; init; } = null!;
    public string ConnectionString { get; init; } = string.Empty;
    public ISettingsSnapshot Snapshot => Sp.GetRequiredService<ISettingsSnapshot>();
    public IOptionsMonitor<T> Mon<T>() where T : class => Sp.GetRequiredService<IOptionsMonitor<T>>();

    public MailArchiverDbContext NewContext() => SettingsKit.NewContext(ConnectionString);

    public async Task<SettingChangeResult> ChangeAsync(string key, string? value, string? password = null, bool reset = false, int userId = 1, string user = "admin")
    {
        using var scope = Sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingsService>()
            .ChangeAsync(key, value, new SettingActor(userId, user), password, reset);
    }

    public async Task<SettingView> ViewAsync(string key)
    {
        using var scope = Sp.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetAllAsync();
        return all.Single(s => s.Key == key);
    }

    public async Task ReloadAsync() => await SettingsLoader.LoadAsync(Sp, NullLogger.Instance);
}

internal static class SettingsKit
{
    /// <summary>Environment variables with this prefix are read as configuration (so a test can PIN a key without touching real settings).</summary>
    public const string EnvPrefix = "MAS_PIN_";

    public static MailArchiverDbContext NewContext(string connectionString)
        => new(new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

    public static SettingsHarness Create(string connectionString, Dictionary<string, string?>? extra = null, Action<Mock<IUserService>>? users = null, bool chainFails = false)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(extra ?? new Dictionary<string, string?>())
            .AddEnvironmentVariables(EnvPrefix)
            .Build();

        var chain = new RecordingChain { Fail = chainFails };
        var log = new RecordingAccessLog();
        var userMock = new Mock<IUserService>();
        userMock.Setup(u => u.GetUserByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) => new User { Id = id, Username = "admin", PasswordHash = "hash" });
        userMock.Setup(u => u.VerifyPassword("secret", "hash")).Returns(true);
        users?.Invoke(userMock);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<IntegrityOptions>(config.GetSection(IntegrityOptions.Section));
        services.Configure<RawMessageOptions>(config.GetSection(RawMessageOptions.RawMessages));
        services.Configure<AttachmentTextOptions>(config.GetSection(AttachmentTextOptions.Section));
        services.Configure<StorageModeOptions>(config.GetSection(StorageModeOptions.Section));
        services.AddSingleton<ISettingsSnapshot, SettingsSnapshot>();
        services.AddSingleton<SettingsPins>();
        services.AddSingleton<IPostConfigureOptions<IntegrityOptions>, SettingsOverlay<IntegrityOptions>>();
        services.AddSingleton<IPostConfigureOptions<RawMessageOptions>, SettingsOverlay<RawMessageOptions>>();
        services.AddSingleton<IPostConfigureOptions<AttachmentTextOptions>, SettingsOverlay<AttachmentTextOptions>>();
        services.AddScoped(_ => NewContext(connectionString));
        services.AddSingleton<IAuditChain>(chain);
        services.AddSingleton<IAccessLogService>(log);
        services.AddSingleton(userMock.Object);
        services.AddScoped<ISettingsService, SettingsService>();
        return new SettingsHarness { Sp = services.BuildServiceProvider(), Chain = chain, Log = log, Users = userMock, ConnectionString = connectionString };
    }
}
