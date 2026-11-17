using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MyFences.Interop;
using MyFences.Models;
using MyFences.Services;

namespace MyFences.Diagnostics;

internal static class SelfTests
{
    internal static async Task<int> RunAsync()
    {
        var results = new List<string>();
        string root = Path.Combine(Path.GetTempPath(), "MyFences-Tests-" + Guid.NewGuid().ToString("N"));
        ConfigurationStore.UseTestRoot(root);
        try
        {
            var store = new ConfigurationStore();
            Check(store.Load().Fences.Count == 0, "New configuration is empty", results);
            var config = new Configuration();
            var state = new FenceState { Title = "Проверка JSON", X = -400, RolledUp = true };
            config.Fences.Add(state);
            store.Save(config);
            Check(store.Load().Fences.Single().Title == state.Title && store.Load().Fences.Single().X == -400 && store.Load().Fences.Single().RolledUp, "Unicode / negative coordinates / roll-up JSON roundtrip", results);
            state.Title = "Updated";
            store.Save(config);
            Check(File.Exists(Path.Combine(root, "config.json.bak")), "Atomic save retains previous version", results);
            File.WriteAllText(Path.Combine(root, "config.json"), "{broken");
            Check(store.Load().Fences.Single().Title == "Проверка JSON" && Directory.GetFiles(root, "config.json.invalid-*").Length == 1, "Corrupt JSON recovery preserves original", results);
            string inputs = Path.Combine(root, "Inputs");
            Directory.CreateDirectory(inputs);
            string file = Path.Combine(inputs, "test.txt");
            File.WriteAllText(file, "original-content");
            var errors = await FileStorage.ImportAsync(state.Id, new[] { file });
            string target = Path.Combine(FileStorage.Folder(state.Id), "test.txt");
            Check(errors.Count == 0 && !File.Exists(file) && File.ReadAllText(target) == "original-content", "File move preserves content and removes source", results);
            File.WriteAllText(file, "second-content");
            errors = await FileStorage.ImportAsync(state.Id, new[] { file });
            Check(errors.Count == 0 && File.ReadAllText(target) == "original-content" && File.ReadAllText(Path.Combine(FileStorage.Folder(state.Id), "test (2).txt")) == "second-content", "Name collision never overwrites destination", results);
            string directory = Path.Combine(inputs, "Folder");
            Directory.CreateDirectory(directory);
            string nested = Path.Combine(directory, "nested.txt");
            File.WriteAllText(nested, "nested");
            errors = await FileStorage.ImportAsync(state.Id, new[] { directory, nested });
            Check(errors.Count == 0 && !Directory.Exists(directory) && File.Exists(Path.Combine(FileStorage.Folder(state.Id), "Folder", "nested.txt")), "Nested selection moves directory only once", results);
            errors = await FileStorage.ImportAsync(state.Id, new[] { target });
            Check(errors.Count == 0 && File.Exists(target), "Dropping an existing item into the same fence is harmless", results);
            errors = await FileStorage.ImportAsync(state.Id, new[] { root });
            Check(errors.Count == 1 && File.Exists(target), "Reject moving an ancestor of storage", results);
            Check(ShellIcons.Get(target) != null, "Native shell icon extracted", results);
            results.Add("PASS: " + results.Count + " checks");
            return 0;
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); return 1; }
        finally
        {
            results.Add("Isolated test data: " + root);
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "self-test-results.txt"), results);
        }
    }

    private static void Check(bool condition, string name, List<string> results)
    {
        if (!condition) throw new InvalidOperationException(name);
        results.Add("PASS " + name);
    }
}
