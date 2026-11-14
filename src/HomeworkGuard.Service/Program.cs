using HomeworkGuard.Core;
using HomeworkGuard.Service.AI;
using HomeworkGuard.Service.Engine;
using HomeworkGuard.Service.Ipc;
using HomeworkGuard.Service.Security;
using HomeworkGuard.Service.Storage;
using HomeworkGuard.Service.Telegram;
using HomeworkGuard.Service.Time;
using HomeworkGuard.Service.Watchdog;

if (args.Contains("--init-config", StringComparer.OrdinalIgnoreCase))
{
    RunInitConfig(args);
    return;
}

var builder = Host.CreateApplicationBuilder(args);

if (!AppBuildConfig.IS_DEV_SANDBOX_MODE)
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = AppBuildConfig.ServiceName;
    });
}

builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<TrustedTimeService>();
builder.Services.AddSingleton<AdminSecurityService>();
builder.Services.AddSingleton<TamperProtectionService>();
builder.Services.AddSingleton<GeminiHomeworkEvaluator>();
builder.Services.AddSingleton<HomeworkGuardCoordinator>();
builder.Services.AddSingleton<TelegramAdminBotService>();
builder.Services.AddSingleton<NamedPipeIpcServer>();
builder.Services.AddHostedService<WatchdogWorker>();

var host = builder.Build();
host.Run();

static void RunInitConfig(string[] args)
{
    string? adminPassword = GetArgValue(args, "--admin-password") ?? Environment.GetEnvironmentVariable("HG_ADMIN_PASSWORD");
    string? geminiKey = GetArgValue(args, "--gemini-key") ?? Environment.GetEnvironmentVariable("HG_GEMINI_API_KEY");
    string? telegramToken = GetArgValue(args, "--telegram-token") ?? Environment.GetEnvironmentVariable("HG_TELEGRAM_TOKEN");
    string? chatId = GetArgValue(args, "--chat-id") ?? Environment.GetEnvironmentVariable("HG_ADMIN_CHAT_ID");
    string? criteria = GetArgValue(args, "--criteria");

    var db = new DatabaseService();

    if (!string.IsNullOrWhiteSpace(adminPassword))
    {
        db.SetAdminPassword(adminPassword);
        db.UpdateSecurityFailures(0, null);
    }
    else if (string.IsNullOrWhiteSpace(db.GetAdminPasswordHash()))
    {
        Console.Error.WriteLine("HATA: İlk kurulum için --admin-password (veya HG_ADMIN_PASSWORD) gereklidir.");
        Environment.ExitCode = 1;
        return;
    }

    if (!string.IsNullOrWhiteSpace(geminiKey))
    {
        db.SetGeminiApiKey(geminiKey);
    }

    if (!string.IsNullOrWhiteSpace(telegramToken))
    {
        db.SetTelegramBotToken(telegramToken);
    }

    if (!string.IsNullOrWhiteSpace(chatId))
    {
        db.SetAdminChatId(chatId);
    }

    if (!string.IsNullOrWhiteSpace(criteria))
    {
        db.UpdateDailyCriteria(criteria);
    }

    if (string.IsNullOrWhiteSpace(db.GetMasterRecoveryKeyHash()) || args.Contains("--rotate-recovery", StringComparer.OrdinalIgnoreCase))
    {
        string recoveryKey = db.GenerateAndStoreNewMasterRecoveryKey();
        Console.WriteLine($"MASTER_RECOVERY_KEY={recoveryKey}");
    }

    db.AddAuditLog("SERVICE_CONFIG_INITIALIZED", "Setup", "Kurulum yapılandırması veritabanına işlendi.");
    Console.WriteLine("HomeworkGuard.Service yapılandırması başarıyla tamamlandı.");
}

static string? GetArgValue(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return null;
}
