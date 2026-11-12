using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using ButchCrawler.Operator;

internal static class SettingsTests
{
    public static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Environment.CurrentDirectory, "Tests", "work", "settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "appsettings.json");
            const string initial = "{\"Crawler\":{\"MaxPages\":100,\"AllowPdf\":false},\"Logging\":{\"LogLevel\":{\"ButchCrawler.App\":\"Debug\"}},\"Extensions\":[\".pdf\",\".txt\"],\"OpenAI\":{\"ApiKey\":\"hidden-plain\",\"EncryptedApiKey\":\"hidden-encrypted\",\"Model\":\"example\"}}";
            try
            {
                File.WriteAllText(path, initial);
                var repository = new SettingsRepository(path);
                check(repository.Rows.All(row => !row.Property.Contains("ApiKey") && !row.Value.Contains("hidden-")), "settings exclude plaintext and encrypted OpenAI keys");
                check(repository.Rows.Any(row => row.Property == "Logging : LogLevel : ButchCrawler.App"), "settings preserve nested properties containing dots");
                repository.Rows.Single(row => row.Property == "Crawler : MaxPages").Value = "250";
                repository.Rows.Single(row => row.Property == "Crawler : AllowPdf").Value = "true";
                repository.Rows.Single(row => row.Property == "Extensions : 0").Value = ".docx";
                repository.Save();
                var json = JsonNode.Parse(File.ReadAllText(path))!;
                check(json["Crawler"]!["MaxPages"]!.GetValue<int>() == 250 && json["Crawler"]!["AllowPdf"]!.GetValue<bool>(), "settings preserve numeric and boolean types");
                check(json["Extensions"]!.AsArray().Count == 2 && json["Extensions"]![0]!.GetValue<string>() == ".docx", "settings edit array values without adding or deleting entries");
                check(json["OpenAI"]!["EncryptedApiKey"]!.GetValue<string>() == "hidden-encrypted" && json["OpenAI"]!["ApiKey"]!.GetValue<string>() == "hidden-plain", "settings preserve excluded keys");
                var snapshot = File.ReadAllText(path);
                repository = new SettingsRepository(path);
                repository.Rows.Single(row => row.Property == "Crawler : MaxPages").Value = "oops";
                try { repository.Save(); throw new Exception("Expected invalid type rejection"); }
                catch (ArgumentException) { check(File.ReadAllText(path) == snapshot, "invalid setting value cannot overwrite file"); }
                repository = new SettingsRepository(path);
                repository.Rows.Single(row => row.Property == "Crawler : MaxPages").Value = "300";
                File.AppendAllText(path, "\n");
                try { repository.Save(); throw new Exception("Expected stale write rejection"); }
                catch (IOException) { check(File.ReadAllText(path) == snapshot + "\n", "settings reject concurrent file changes"); }
                var window = new SettingsWindow(path);
                var grid = (DataGrid)typeof(SettingsWindow).GetField("_grid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                check(grid.Columns.Count == 2 && grid.Columns[0].IsReadOnly && !grid.Columns[1].IsReadOnly && !grid.CanUserAddRows && !grid.CanUserDeleteRows,
                    "settings grid has two columns, editable values, and no add or delete");
                ((IEnumerable<SettingRow>)grid.ItemsSource).Single(row => row.Property == "Crawler : MaxPages").Value = "400";
                window.Close();
                check(JsonNode.Parse(File.ReadAllText(path))!["Crawler"]!["MaxPages"]!.GetValue<int>() == 400, "settings window saves existing edits on close");
            }
            catch (Exception ex) { failure = ex; }
            finally { Directory.Delete(directory, recursive: true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
}
