using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ButchCrawler.App;

public sealed record OpenAiFeatureSelection(bool AiEnabled, bool HandwritingEnabled, bool AudioEnabled, bool IndexerEnabled)
{
    public static OpenAiFeatureSelection ManualTools { get; } = new(true, true, false, false);
    public static OpenAiFeatureSelection Disabled { get; } = new(false, false, false, false);
}

public sealed class OpenAiKeyStore
{
    private readonly string _settingsPath;
    private readonly string _keyPath;
    private readonly string _legacyPath;
    public OpenAiKeyStore(string? settingsPath = null, string? legacyPath = null, string? keyPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(Environment.CurrentDirectory, "appsettings.json");
        _keyPath = keyPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HistoryCrawler", "openai-key.dpapi");
        _legacyPath = legacyPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ButchCrawler", "openai-key.dpapi");
    }

    private JsonObject ReadSettings() => File.Exists(_settingsPath)
        ? JsonNode.Parse(File.ReadAllText(_settingsPath), documentOptions: new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new InvalidDataException("Settings must contain a JSON object.")
        : new JsonObject();

    public string ReadEncrypted()
    {
        var current = ReadCiphertext(_keyPath);
        if (!string.IsNullOrEmpty(current)) return current;

        var settings = ReadSettings();
        if (settings["OpenAI"] is JsonObject openAi)
        {
            var encrypted = openAi["EncryptedApiKey"]?.GetValue<string>()?.Trim() ?? string.Empty;
            var plaintext = openAi["ApiKey"]?.GetValue<string>()?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(encrypted)) ValidateCiphertext(encrypted);
            else if (!string.IsNullOrEmpty(plaintext)) encrypted = Encrypt(plaintext);

            if (!string.IsNullOrEmpty(encrypted))
            {
                WriteCiphertext(encrypted);
                RemoveKeySettings(settings);
                WriteSettings(settings);
                return encrypted;
            }

            if (RemoveKeySettings(settings)) WriteSettings(settings);
        }

        if (!string.IsNullOrEmpty(_legacyPath) && File.Exists(_legacyPath))
        {
            var legacy = ReadCiphertext(_legacyPath);
            if (!string.IsNullOrEmpty(legacy))
            {
                ValidateCiphertext(legacy);
                WriteCiphertext(legacy);
                File.Delete(_legacyPath);
                return legacy;
            }
        }
        return string.Empty;
    }

    public string? ReadKey()
    {
        var encrypted = ReadEncrypted();
        return string.IsNullOrEmpty(encrypted) ? null : Decrypt(encrypted);
    }

    public static string Decrypt(string encrypted)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saved API keys require Windows user encryption.");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Save(string key, OpenAiFeatureSelection? features = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saved API keys require Windows user encryption.");
        key = key.Trim();
        if (key.Any(char.IsWhiteSpace)) throw new ArgumentException("The API key cannot contain whitespace.");
        var settings = ReadSettings();
        var settingsChanged = RemoveKeySettings(settings);
        if (features != null)
        {
            var ai = settings["AI"] as JsonObject;
            if (settings["AI"] != null && ai == null) throw new InvalidDataException("AI settings must be a JSON object.");
            if (ai == null) { ai = new JsonObject(); settings["AI"] = ai; }
            ai["Enabled"] = features.AiEnabled;
            ai["HandwritingEnabled"] = features.AiEnabled && features.HandwritingEnabled;

            var audio = settings["Audio"] as JsonObject;
            if (settings["Audio"] != null && audio == null) throw new InvalidDataException("Audio settings must be a JSON object.");
            if (audio == null) { audio = new JsonObject(); settings["Audio"] = audio; }
            audio["Enabled"] = features.AudioEnabled;

            var indexer = settings["Indexer"] as JsonObject;
            if (settings["Indexer"] != null && indexer == null) throw new InvalidDataException("Indexer settings must be a JSON object.");
            if (indexer == null) { indexer = new JsonObject(); settings["Indexer"] = indexer; }
            indexer["Enabled"] = features.IndexerEnabled;
            settingsChanged = true;
        }

        if (key.Length == 0)
        {
            if (File.Exists(_keyPath)) File.Delete(_keyPath);
        }
        else WriteCiphertext(Encrypt(key));

        if (settingsChanged) WriteSettings(settings);
    }

    private static string Encrypt(string key)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saved API keys require Windows user encryption.");
        var bytes = Encoding.UTF8.GetBytes(key);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void ValidateCiphertext(string encrypted)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saved API keys require Windows user encryption.");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static string ReadCiphertext(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;

    private void WriteCiphertext(string encrypted)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_keyPath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, encrypted);
            File.Move(temporary, _keyPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool RemoveKeySettings(JsonObject settings)
    {
        if (settings["OpenAI"] == null) return false;
        if (settings["OpenAI"] is not JsonObject openAi) throw new InvalidDataException("OpenAI settings must be a JSON object.");
        var changed = openAi.Remove("ApiKey") | openAi.Remove("EncryptedApiKey");
        if (openAi.Count == 0) changed |= settings.Remove("OpenAI");
        return changed;
    }

    private void WriteSettings(JsonObject settings)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_settingsPath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _settingsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

}
