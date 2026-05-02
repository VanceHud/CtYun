using CtYun.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CtYun;

internal sealed class CtYunConfigStore
{
    private const string PasswordMask = "********";
    private readonly object _sync = new();
    private AppConfig _config;

    public CtYunConfigStore()
    {
        DataDir = GetDataDir();
        Directory.CreateDirectory(DataDir);

        ConfigPath = Environment.GetEnvironmentVariable("CTYUN_CONFIG");
        if (string.IsNullOrWhiteSpace(ConfigPath))
        {
            ConfigPath = Path.Combine(DataDir, "accounts.json");
        }

        _config = LoadFromDisk() ?? LoadFromEnvironment() ?? new AppConfig();
        _config = Normalize(_config, keepExistingPasswords: false);

        if (!File.Exists(ConfigPath) && _config.Accounts.Count > 0)
        {
            Save(_config);
        }
    }

    public string DataDir { get; }

    public string ConfigPath { get; }

    public AppConfig GetConfig()
    {
        lock (_sync)
        {
            return Clone(_config);
        }
    }

    public AppConfig GetSanitizedConfig()
    {
        var config = GetConfig();
        foreach (var account in config.Accounts)
        {
            account.Password = string.IsNullOrWhiteSpace(account.Password) ? "" : PasswordMask;
        }

        return config;
    }

    public async Task SaveAsync(AppConfig config, CancellationToken ct)
    {
        var normalized = Normalize(config, keepExistingPasswords: true);
        var json = JsonSerializer.Serialize(normalized, AppJsonSerializerContext.Default.AppConfig);
        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(ConfigPath, json, ct);
        lock (_sync)
        {
            _config = Clone(normalized);
        }
    }

    public AppConfig Normalize(AppConfig config, bool keepExistingPasswords)
    {
        config ??= new AppConfig();
        var existing = keepExistingPasswords ? GetConfig() : new AppConfig();
        var normalized = new AppConfig
        {
            KeepAliveSeconds = Math.Max(10, config.KeepAliveSeconds <= 0 ? 60 : config.KeepAliveSeconds),
            Accounts = []
        };

        foreach (var account in config.Accounts ?? [])
        {
            var normalizedAccount = NormalizeAccount(account, keepExistingPasswords, existing);
            if (!string.IsNullOrWhiteSpace(normalizedAccount.User))
            {
                normalized.Accounts.Add(normalizedAccount);
            }
        }

        return normalized;
    }

    public AccountConfig NormalizeAccount(AccountConfig account, bool keepExistingPassword)
    {
        return NormalizeAccount(account, keepExistingPassword, GetConfig());
    }

    public string ResolveDeviceCode(AccountConfig account)
    {
        if (!string.IsNullOrWhiteSpace(account.DeviceCode))
        {
            return account.DeviceCode.Trim();
        }

        var devicesDir = Path.Combine(DataDir, "devices");
        Directory.CreateDirectory(devicesDir);
        var deviceCodePath = Path.Combine(devicesDir, SafeName(FirstNotEmpty(account.Name, account.User)));
        deviceCodePath = Path.ChangeExtension(deviceCodePath, ".txt");
        if (!File.Exists(deviceCodePath))
        {
            File.WriteAllText(deviceCodePath, "web_" + GenerateRandomString(32));
        }

        return File.ReadAllText(deviceCodePath).Trim();
    }

    private AccountConfig NormalizeAccount(AccountConfig account, bool keepExistingPassword, AppConfig existingConfig)
    {
        account ??= new AccountConfig();
        var user = account.User?.Trim();
        var name = FirstNotEmpty(account.Name?.Trim(), user);
        var existing = FindExistingAccount(existingConfig, name, user);
        var password = account.Password;
        if (keepExistingPassword && (string.IsNullOrWhiteSpace(password) || password == PasswordMask))
        {
            password = existing?.Password;
        }

        var normalized = new AccountConfig
        {
            Name = name,
            User = user,
            Password = password?.Trim(),
            DeviceCode = FirstNotEmpty(account.DeviceCode?.Trim(), existing?.DeviceCode)
        };
        if (!string.IsNullOrWhiteSpace(normalized.User))
        {
            normalized.DeviceCode = ResolveDeviceCode(normalized);
        }

        return normalized;
    }

    private AppConfig LoadFromDisk()
    {
        if (!File.Exists(ConfigPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppConfig);
            Utility.WriteLine(ConsoleColor.Green, $"已读取配置文件：{ConfigPath}");
            return config;
        }
        catch (Exception ex)
        {
            Utility.WriteLine(ConsoleColor.Red, $"读取配置文件失败：{ex.Message}");
            return new AppConfig();
        }
    }

    private AppConfig LoadFromEnvironment()
    {
        var user = Environment.GetEnvironmentVariable("APP_USER");
        var password = Environment.GetEnvironmentVariable("APP_PASSWORD");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        return new AppConfig
        {
            Accounts =
            [
                new AccountConfig
                {
                    Name = Environment.GetEnvironmentVariable("APP_NAME"),
                    User = user,
                    Password = password,
                    DeviceCode = Environment.GetEnvironmentVariable("DEVICECODE")
                }
            ]
        };
    }

    private void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, AppJsonSerializerContext.Default.AppConfig);
        File.WriteAllText(ConfigPath, json);
    }

    private static AccountConfig FindExistingAccount(AppConfig config, string name, string user)
    {
        return config.Accounts.FirstOrDefault(account =>
            (!string.IsNullOrWhiteSpace(user) && string.Equals(account.User, user, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(name) && string.Equals(account.Name, name, StringComparison.OrdinalIgnoreCase)));
    }

    private static AppConfig Clone(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, AppJsonSerializerContext.Default.AppConfig);
        return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppConfig) ?? new AppConfig();
    }

    private static string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Repeat(chars, length).Select(s => s[RandomNumberGenerator.GetInt32(s.Length)]).ToArray());
    }

    private static string SafeName(string value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "default" : value;
        var builder = new StringBuilder(source.Length);
        foreach (var ch in source)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        return builder.ToString();
    }

    private static string FirstNotEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string GetDataDir()
    {
        var dataDir = Environment.GetEnvironmentVariable("CTYUN_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            return dataDir;
        }

        return IsRunningInContainer() ? "/app/data" : AppContext.BaseDirectory;
    }

    private static bool IsRunningInContainer() => File.Exists("/.dockerenv");
}
