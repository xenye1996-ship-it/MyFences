using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;

namespace MyFences.Services;

internal static class FileStorage
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static string StorageRoot => Path.Combine(ConfigurationStore.Root, "Storage");

    internal static string Folder(Guid id)
    {
        Directory.CreateDirectory(StorageRoot);
        File.SetAttributes(StorageRoot, File.GetAttributes(StorageRoot) | FileAttributes.Hidden);
        string path = Path.Combine(StorageRoot, id.ToString("D"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static async Task<List<string>> ImportAsync(Guid id, string[] sources)
    {
        await Gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                var errors = new List<string>();
                string destination = Folder(id);
                var paths = sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p.Length).ToList();
                var selectedParents = paths.Where(Directory.Exists).ToList();
                foreach (string source in paths)
                {
                    if (selectedParents.Any(parent => !string.Equals(parent, source, StringComparison.OrdinalIgnoreCase) && IsInside(source, parent))) continue;
                    try
                    {
                        if (string.Equals(Path.GetDirectoryName(source), destination, StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(source, Path.GetPathRoot(source), StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(source, ConfigurationStore.Root, StringComparison.OrdinalIgnoreCase) ||
                            IsInside(destination, source) || IsInside(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), source))
                            throw new IOException("Эту системную или родительскую папку перемещать нельзя.");
                        if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException("Объект больше не существует.");
                        string target = UniquePath(destination, Path.GetFileName(source), Directory.Exists(source));
                        if (Directory.Exists(source))
                            FileSystem.MoveDirectory(source, target, UIOption.OnlyErrorDialogs, UICancelOption.ThrowException);
                        else
                            FileSystem.MoveFile(source, target, UIOption.OnlyErrorDialogs, UICancelOption.ThrowException);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(Path.GetFileName(source) + ": " + ex.Message);
                        ConfigurationStore.Log(ex);
                    }
                }
                return errors;
            });
        }
        finally { Gate.Release(); }
    }

    private static bool IsInside(string path, string directory) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string UniquePath(string directory, string name, bool folder)
    {
        string stem = folder ? name : Path.GetFileNameWithoutExtension(name);
        string extension = folder ? "" : Path.GetExtension(name);
        string candidate = Path.Combine(directory, name);
        for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
        return candidate;
    }
}
