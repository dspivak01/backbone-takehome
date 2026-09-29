using System.Security.Cryptography;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Ingest;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Web;

/// <summary>One file sent from the page: the name the reviewer's machine gave it, kept as text only, and its content.</summary>
public sealed record UploadedFile(string Name, byte[] Content);

/// <summary>
/// Files added from the page. Only text files of up to 1 MB are taken. Each is saved in a new
/// folder inside the output folder under a name this program makes up, so the name a file
/// arrived with, which could be "../../x.txt", never becomes part of a path. That name is kept as
/// text in the database, for the Documents screen to show.
/// </summary>
public static class Uploads
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxFiles = 100;
    public const string Command = "upload";
    private const string Rule = "Only text files ending in .txt, of up to 1 MB each, can be added.";

    /// <summary>Refuses the whole upload, with a plain message, if any file is not a text file of up to 1 MB.</summary>
    public static void Check(IReadOnlyList<UploadedFile> files)
    {
        if (files.Count == 0) throw new RequestException("Choose one or more text files first.");
        if (files.Count > MaxFiles) throw new RequestException($"Nothing was added. At most {MaxFiles} files can be added at a time. Add them in smaller groups, or name the folder that holds them.");
        foreach (var file in files)
        {
            if (file.Content.Length > MaxBytes) throw new RequestException(TooLarge(file.Name));
            if (!HasTextName(file.Name)) throw new RequestException($"Nothing was added. \"{Shown(file.Name)}\" does not end in .txt. {Rule}");
            if (!DocumentRegistry.IsText(file.Content)) throw new RequestException($"Nothing was added. \"{Shown(file.Name)}\" is not a text file. {Rule}");
            if (file.Content.Length == 0) throw new RequestException($"Nothing was added. \"{Shown(file.Name)}\" is empty.");
        }
    }

    public static string TooLarge(string name) => $"Nothing was added. \"{Shown(name)}\" is larger than 1 MB. {Rule}";

    /// <summary>The same rule the ingest of a folder uses: a name ending in .txt, or with no extension. The name is only read, never used as a path.</summary>
    private static bool HasTextName(string name)
    {
        var last = name.Split('/', '\\').Last();
        var dot = last.LastIndexOf('.');
        return dot <= 0 || last[dot..].Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A file's own name as the page shows it: the text it arrived with, shortened if very long.</summary>
    public static string Shown(string name) => name.Length > 200 ? name[..200] + "…" : name;

    /// <summary>
    /// Saves checked files in a new folder inside the output folder, as 001.txt, 002.txt and so on,
    /// and records the name each arrived with. Returns the folder and, for each saved path, that name.
    /// </summary>
    public static (string Folder, Dictionary<string, string> Names) Save(Database database, string outputFolder, IReadOnlyList<UploadedFile> files)
    {
        Check(files);
        var folder = Path.Combine(outputFolder, "uploads", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}");
        Directory.CreateDirectory(folder);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++)
        {
            var path = Path.Combine(folder, $"{i + 1:000}.txt");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.Write(files[i].Content);
            names[path] = Shown(files[i].Name);
        }
        database.RecordRun(Command, DateTime.UtcNow, 0, Json.Write(new UploadRecord
        {
            Folder = folder,
            Files = names.Select(n => new UploadName { Saved = n.Key, Name = n.Value }).ToList(),
        }));
        return (folder, names);
    }

    /// <summary>The name each uploaded file arrived with, by the path it was saved at.</summary>
    public static Dictionary<string, string> OriginalNames(Database database)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var run in database.ListRuns().Where(r => r.Command == Command))
        {
            try
            {
                foreach (var file in Json.Read<UploadRecord>(run.Json).Files) names[file.Saved] = file.Name;
            }
            catch (Exception)
            {
                // A record that cannot be read leaves those files shown by their saved names.
            }
        }
        return names;
    }

    public sealed class UploadRecord
    {
        public string Folder { get; set; } = "";
        public List<UploadName> Files { get; set; } = [];
    }

    public sealed class UploadName
    {
        public string Saved { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
