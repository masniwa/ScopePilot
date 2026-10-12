namespace ScopePilot.Domain;

public sealed class ExplorationDocumentSettings
{
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public EvidenceReference? Source { get; set; }
}
