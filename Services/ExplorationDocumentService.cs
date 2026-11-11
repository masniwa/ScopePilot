using ScopePilot.Domain;
using System.Text;
using System.Text.Json;

namespace ScopePilot.Services;

public static class ExplorationDocumentService
{
    public const string RunFileName = "reference-document.txt";
    public const int MaxDocuments = 20;
    public const string IndexFileName = "reference-documents/index.json";

    public static IReadOnlyList<ExplorationReferenceDocument> Documents(ExplorationDocumentSettings settings) =>
        settings.Files is { Count: > 0 } files ? files : string.IsNullOrWhiteSpace(settings.Content) ? [] :
        [new() { FileName = settings.FileName, Content = settings.Content, Source = settings.Source }];

    public static string DisplayName(ExplorationDocumentSettings settings) =>
        string.Join("、", Documents(settings).Select(x => x.FileName));

    public static string CombinedContent(ExplorationDocumentSettings settings) => Combine(Documents(settings));

    private static string Combine(IReadOnlyList<ExplorationReferenceDocument> documents)
    {
        if (documents.Count > MaxDocuments) throw new InvalidDataException($"探索用資料は最大{MaxDocuments}件です。");
        foreach (var document in documents)
            if (string.IsNullOrWhiteSpace(document.Content) || document.Content.Contains('\0') || document.Content.Length > TextDocumentReader.MaxCharacters)
                throw new InvalidDataException("探索用資料は空でないUTF-8テキスト文書にしてください。");
        var content = documents.Count == 1 ? documents[0].Content : string.Join("\n\n", documents.Select((document, index) =>
            $"[reference-document {index + 1:D3}: {JsonSerializer.Serialize(document.FileName)}]\n{document.Content}\n[/reference-document {index + 1:D3}]"));
        if (content.Length > TextDocumentReader.MaxCharacters)
            throw new InvalidDataException($"探索用資料は見出しを含め合計{TextDocumentReader.MaxCharacters:N0}文字以内にしてください。資料を自動で切り捨てることはありません。");
        return content;
    }

    public static async Task<List<ExplorationReferenceDocument>> ImportManyAsync(EngagementProject project, IEnumerable<string> paths,
        IReadOnlyList<ExplorationReferenceDocument> selected)
    {
        // Read/validate the whole batch before creating any project artifacts.
        var additions = new List<(string Path, ExplorationReferenceDocument Document)>();
        foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var document = new ExplorationReferenceDocument { FileName = Path.GetFileName(path), Content = await TextDocumentReader.ReadAsync(path) };
            if (selected.Concat(additions.Select(x => x.Document)).Any(x => x.FileName == document.FileName && x.Content == document.Content)) continue;
            additions.Add((path, document));
        }
        var result = selected.ToList();
        _ = Combine(result.Concat(additions.Select(x => x.Document)).ToArray());
        var copied = new List<EvidenceReference>();
        try
        {
            foreach (var addition in additions)
            {
                var source = (await ProjectPaths.ImportAsync(project, addition.Path, "exploration-document")) with { Role = project.ActiveRole };
                copied.Add(source);
                var imported = new ExplorationReferenceDocument { FileName = addition.Document.FileName,
                    Content = await TextDocumentReader.ReadAsync(ProjectPaths.Resolve(project, source.LocalPath)), Source = source };
                // The source might change between preflight and copying.
                if (imported.Content != addition.Document.Content) throw new InvalidDataException("資料が読み込み中に変更されました。もう一度選択してください。");
                result.Add(new() { FileName = imported.FileName, Content = imported.Content, Source = imported.Source });
            }
            return result;
        }
        catch
        {
            foreach (var source in copied) File.Delete(ProjectPaths.Resolve(project, source.LocalPath));
            throw;
        }
    }

    public static async Task WriteSnapshotsAsync(ExplorationDocumentSettings settings, string directory)
    {
        var documents = Documents(settings);
        if (documents.Count == 0) return;
        var combined = Combine(documents);
        await File.WriteAllTextAsync(Path.Combine(directory, RunFileName), combined, new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(directory, "reference-documents"));
        var index = new List<object>();
        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            var file = $"reference-documents/{i + 1:D3}.txt";
            await File.WriteAllTextAsync(Path.Combine(directory, file), document.Content, new UTF8Encoding(false));
            index.Add(new { id = i + 1, document.FileName, file, source = document.Source, sha256 = DocumentHintService.Sha256(document.Content), characters = document.Content.Length });
        }
        await File.WriteAllTextAsync(Path.Combine(directory, IndexFileName), JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task<ExplorationDocumentSettings> ImportAsync(EngagementProject project, string path)
    {
        // Validate before copying so unsupported inputs do not leave project artifacts.
        _ = await TextDocumentReader.ReadAsync(path);
        var source = await ProjectPaths.ImportAsync(project, path, "exploration-document");
        var content = await TextDocumentReader.ReadAsync(ProjectPaths.Resolve(project, source.LocalPath));
        return new()
        {
            FileName = Path.GetFileName(path),
            Content = content,
            Source = source with { Role = project.ActiveRole }
        };
    }
}
