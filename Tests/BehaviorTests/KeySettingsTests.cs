using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using ButchCrawler.App;
using ButchCrawler.Operator;

internal static class KeySettingsTests
{
    public static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Environment.CurrentDirectory, "Tests", "work", "key-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "appsettings.json");
            var keyPath = Path.Combine(directory, "openai-key.dpapi");
            try
            {
                var store = new OpenAiKeyStore(path, legacyPath: string.Empty, keyPath: keyPath);
                check(store.ReadKey() == null, "missing saved key is optional");
                var emptyWindow = new OpenAiKeyWindow(store);
                var emptyBox = (TextBox)typeof(OpenAiKeyWindow).GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(emptyWindow)!;
                var emptyDecrypt = (Button)typeof(OpenAiKeyWindow).GetField("_decrypt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(emptyWindow)!;
                check(!emptyBox.IsReadOnly && emptyBox.IsEnabled && emptyBox.Focusable && !emptyDecrypt.IsEnabled,
                    "no saved key opens enabled for entry without decrypting");
                emptyBox.Text = "new-test-credential";
                emptyWindow.Close();
                check(store.ReadKey() == "new-test-credential" && File.Exists(keyPath),
                    "first key entry saves outside settings without decrypting");

                var promptedPath = Path.Combine(directory, "prompted-settings.json");
                var promptedKeyPath = Path.Combine(directory, "prompted-key.dpapi");
                File.WriteAllText(promptedPath, "{\"AI\":{\"Enabled\":false,\"HandwritingEnabled\":false},\"Audio\":{\"Enabled\":false},\"Indexer\":{\"Enabled\":false}}");
                var promptedStore = new OpenAiKeyStore(promptedPath, legacyPath: string.Empty, keyPath: promptedKeyPath);
                var promptedWindow = new OpenAiKeyWindow(promptedStore,
                    () => new OpenAiFeatureSelection(true, true, false, false));
                var promptedBox = (TextBox)typeof(OpenAiKeyWindow).GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(promptedWindow)!;
                promptedBox.Text = "prompted-test-credential"; promptedWindow.Close();
                var promptedJson = JsonNode.Parse(File.ReadAllText(promptedPath))!;
                check(promptedJson["AI"]!["Enabled"]!.GetValue<bool>() && promptedJson["AI"]!["HandwritingEnabled"]!.GetValue<bool>() &&
                    !promptedJson["Audio"]!["Enabled"]!.GetValue<bool>() && !promptedJson["Indexer"]!["Enabled"]!.GetValue<bool>(),
                    "adding a key applies chosen manual AI settings while automatic-cost features stay off");
                var removalWindow = new OpenAiKeyWindow(promptedStore, removedKeyPrompt: () => System.Windows.MessageBoxResult.Yes);
                typeof(OpenAiKeyWindow).GetMethod("Unlock", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(removalWindow, null);
                var removalBox = (TextBox)typeof(OpenAiKeyWindow).GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(removalWindow)!;
                removalBox.Clear(); removalWindow.Close();
                promptedJson = JsonNode.Parse(File.ReadAllText(promptedPath))!;
                check(promptedStore.ReadKey() == null && !promptedJson["AI"]!["Enabled"]!.GetValue<bool>() &&
                    !promptedJson["AI"]!["HandwritingEnabled"]!.GetValue<bool>() && !promptedJson["Audio"]!["Enabled"]!.GetValue<bool>() &&
                    !promptedJson["Indexer"]!["Enabled"]!.GetValue<bool>(), "deleting a key can turn off every paid AI feature without deleting data");
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, "{\"Crawler\":{\"MaxPages\":123},\"OpenAI\":{\"ApiKey\":\"old-plaintext\",\"Other\":true}}");
                const string first = "test-key-not-a-real-credential";
                store.Save(first);
                var encrypted = store.ReadEncrypted();
                check(!encrypted.Contains(first) && store.ReadKey() == first && File.ReadAllText(keyPath) == encrypted,
                    "DPAPI roundtrip stores ciphertext only in the private key file");
                var json = JsonNode.Parse(File.ReadAllText(path))!;
                check(json["OpenAI"]!["EncryptedApiKey"] == null && json["OpenAI"]!["ApiKey"] == null &&
                    !File.ReadAllText(path).Contains(first) && !File.ReadAllText(path).Contains(encrypted),
                    "JSON contains no plaintext or encrypted key");
                check(json["Crawler"]!["MaxPages"]!.GetValue<int>() == 123 && json["OpenAI"]!["Other"]!.GetValue<bool>(),
                    "saving key preserves unrelated JSON settings");
                try { OpenAiKeyStore.Decrypt(Convert.ToBase64String(new byte[32])); throw new Exception("Expected invalid ciphertext rejection"); }
                catch (System.Security.Cryptography.CryptographicException) { check(true, "invalid encrypted key is rejected"); }
                try { store.Save("invalid key"); throw new Exception("Expected whitespace rejection"); }
                catch (ArgumentException) { check(store.ReadEncrypted() == encrypted, "invalid edit preserves saved key"); }
                var window = new OpenAiKeyWindow(store);
                var box = (TextBox)typeof(OpenAiKeyWindow).GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                check(box.IsReadOnly && box.Text == encrypted && !box.IsUndoEnabled, "key editor opens locked with ciphertext and no undo history");
                typeof(OpenAiKeyWindow).GetMethod("Unlock", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                check(!box.IsReadOnly && box.IsEnabled && box.Focusable && box.Text == first, "decrypt unlocks enabled plaintext editing");
                box.Text = "updated-test-credential";
                window.Close();
                check(store.ReadKey() == "updated-test-credential" && box.Text == string.Empty, "closing saves encrypted edits and clears plaintext control");
                var locked = new OpenAiKeyWindow(store);
                encrypted = store.ReadEncrypted(); locked.Close();
                check(store.ReadEncrypted() == encrypted, "closing locked dialog leaves key unchanged");
                store.Save(""); check(store.ReadKey() == null && File.Exists(path) && !File.Exists(keyPath),
                    "clearing key preserves JSON settings and deletes the private key file");
                var clearedWindow = new OpenAiKeyWindow(store);
                var clearedBox = (TextBox)typeof(OpenAiKeyWindow).GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clearedWindow)!;
                check(!clearedBox.IsReadOnly && clearedBox.IsEnabled, "cleared saved key reopens ready for entry");
                clearedWindow.Close();
                var migratedSettings = Path.Combine(directory, "migrated-settings.json");
                var migratedKey = Path.Combine(directory, "migrated-key.dpapi");
                var migrationJson = new JsonObject { ["OpenAI"] = new JsonObject { ["EncryptedApiKey"] = encrypted },
                    ["Crawler"] = new JsonObject { ["MaxPages"] = 123 } };
                File.WriteAllText(migratedSettings, migrationJson.ToJsonString());
                var migrated = new OpenAiKeyStore(migratedSettings, legacyPath: string.Empty, keyPath: migratedKey);
                check(migrated.ReadKey() == "updated-test-credential" && File.Exists(migratedKey) &&
                    JsonNode.Parse(File.ReadAllText(migratedSettings))!["OpenAI"] == null,
                    "encrypted JSON key migrates to the private key file and is removed from settings");
                migrated.Save("");

                var legacy = Path.Combine(directory, "old-key.dpapi");
                var legacyDestination = Path.Combine(directory, "legacy-destination.dpapi");
                File.WriteAllText(legacy, encrypted);
                var legacyStore = new OpenAiKeyStore(Path.Combine(directory, "legacy-settings.json"), legacy, legacyDestination);
                check(legacyStore.ReadKey() == "updated-test-credential" && File.Exists(legacyDestination) && !File.Exists(legacy),
                    "legacy encrypted file migrates to the History Crawler key location");
                legacyStore.Save("");
                check(legacyStore.ReadKey() == null, "cleared key does not remigrate a legacy copy");
                var defaults = JsonNode.Parse(File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "ButchCrawler.App", "appsettings.json")))!;
                check(!defaults["AI"]!["Enabled"]!.GetValue<bool>() && !defaults["AI"]!["HandwritingEnabled"]!.GetValue<bool>() &&
                    !defaults["Audio"]!["Enabled"]!.GetValue<bool>() && !defaults["Indexer"]!["Enabled"]!.GetValue<bool>() &&
                    defaults["OpenAI"] == null,
                    "supplied settings keep all paid AI features off by default");
                var searchOnlyPath = Path.Combine(directory, "search-only.json");
                File.WriteAllText(searchOnlyPath, "{\"ConnectionStrings\":{\"DefaultConnection\":\"Server=(local);Database=unused;Trusted_Connection=True;\"},\"AI\":{\"Enabled\":false},\"Audio\":{\"Enabled\":false},\"Indexer\":{\"Enabled\":false},\"OpenAI\":{\"EncryptedApiKey\":\"not-valid-ciphertext\"}}");
                using (var searchOnly = new SqlAiResearchWorkspace(searchOnlyPath))
                    check(searchOnly != null, "free collection workspace does not decrypt or require an OpenAI key");
                File.WriteAllText(path, "invalid-json");
                try { store.Save(first); throw new Exception("Expected malformed settings rejection"); }
                catch (System.Text.Json.JsonException) { check(File.ReadAllText(path) == "invalid-json", "malformed JSON is not overwritten"); }
            }
            catch (Exception ex) { failure = ex; }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
}
