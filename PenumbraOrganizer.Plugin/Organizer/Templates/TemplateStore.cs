namespace PenumbraOrganizer.Plugin.Organizer.Templates;

public sealed record StoredTemplate(
    string FileName,
    ValidatedOrganizationTemplate Template,
    IReadOnlyList<TemplateWarning> Warnings);

public sealed record TemplateStoreListing(
    IReadOnlyList<StoredTemplate> Templates,
    IReadOnlyList<string> UnreadableFiles);

/// <summary>
/// Reads and writes the templates/ folder. Every file in it arrived from outside -- a Discord
/// attachment, a blog download -- so one unreadable file must never make the rest unavailable:
/// it is reported by name and skipped.
///
/// Filenames come from TemplateSlug, never from the document's raw name, and the document's own
/// `name` stays authoritative for display. A save never overwrites an existing file.
/// </summary>
public sealed class TemplateStore(string directory)
{
    public string Directory { get; } = directory;

    public TemplateStoreListing List()
    {
        if (!System.IO.Directory.Exists(Directory))
            return new TemplateStoreListing([], []);

        var templates = new List<StoredTemplate>();
        var unreadable = new List<string>();

        string[] paths;
        try
        {
            paths = System.IO.Directory.GetFiles(Directory, "*.json");
        }
        catch (IOException)
        {
            // The directory vanished or became unreadable between the Exists check above and
            // now. List() is called from a draw method, so this must degrade to an empty
            // listing rather than throw a frame.
            return new TemplateStoreListing([], []);
        }
        catch (UnauthorizedAccessException)
        {
            return new TemplateStoreListing([], []);
        }

        foreach (var path in paths.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(path);

            FileInfo info;
            try
            {
                info = new FileInfo(path);
            }
            catch (IOException)
            {
                unreadable.Add(fileName);
                continue;
            }

            // The share-code transport caps its payload during inflation; a file dropped into
            // this folder must be held to the same ceiling. Without this, opening the tab reads
            // an arbitrarily large stranger-supplied file into memory inside a draw call, where
            // an OutOfMemoryException is not catchable by the IO guards below.
            if (info.Length > TemplateLimits.MaxDecompressedBytes)
            {
                unreadable.Add(fileName);
                continue;
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (IOException)
            {
                unreadable.Add(fileName);
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                unreadable.Add(fileName);
                continue;
            }

            var decoded = TemplateCodec.DecodeJson(json);
            if (!decoded.Succeeded)
            {
                unreadable.Add(fileName);
                continue;
            }

            templates.Add(new StoredTemplate(fileName, decoded.Template!, decoded.Warnings));
        }

        return new TemplateStoreListing(templates, unreadable);
    }

    /// <summary>
    /// Validates before writing, so an invalid document never reaches disk, then writes atomically
    /// under a filename that cannot collide with an existing one. Returns the filename used.
    /// </summary>
    public string Save(string json, string displayName)
    {
        var decoded = TemplateCodec.DecodeJson(json);
        if (!decoded.Succeeded)
            throw new ArgumentException($"Template is not valid: {decoded.ErrorDetail}", nameof(json));

        System.IO.Directory.CreateDirectory(Directory);

        var taken = System.IO.Directory.GetFiles(Directory, "*.json")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fileName = TemplateSlug.MakeUnique(TemplateSlug.From(displayName), taken) + ".json";
        var target = Path.Combine(Directory, fileName);
        var temp = target + ".tmp";

        File.WriteAllText(temp, json);
        File.Move(temp, target, overwrite: false);

        return fileName;
    }
}
