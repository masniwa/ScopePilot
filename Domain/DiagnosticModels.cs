namespace ScopePilot.Domain;

public enum HandoffTarget { BurpSuite, Zap }
public enum DiagnosisState
{
    TargetAlertEffectUnconfirmed = 5
}
public sealed record EvidenceReference(string LocalPath, string Sha256, string Pointer, string Kind, string Sensitivity)
{
    public string Role { get; init; } = "";
}
public sealed class DiagnosticToolSettings
{
    public HandoffTarget Target { get; set; }
    public string ZapJarPath { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public bool EnableZapActiveScan { get; set; }
    public string ActiveScanStrength { get; set; } = ZapActiveScanProfile.DefaultStrength;
    public string ActiveScanThreshold { get; set; } = ZapActiveScanProfile.DefaultThreshold;
    public int MaxActiveScanTargets { get; set; } = ZapActiveScanProfile.DefaultMaxTargets;
    public bool IncludeReferenceHars { get; set; }
    public bool UseRecordedAuthentication { get; set; }
}

public static class ZapActiveScanProfile
{
    public const string DefaultStrength = "Low";
    public const string DefaultThreshold = "Medium";
    public const int DefaultMaxTargets = 100;
    public const int MaxTargetsLimit = 500;
    public static readonly IReadOnlyList<string> Strengths = ["Low", "Default", "Medium", "High"];
    public static readonly IReadOnlyList<string> Thresholds = ["Low", "Medium", "High", "Default"];
    public static string NormalizeStrength(string? value) => Strengths.Contains(value, StringComparer.OrdinalIgnoreCase)
        ? Strengths.First(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)) : DefaultStrength;
    public static string NormalizeThreshold(string? value) => Thresholds.Contains(value, StringComparer.OrdinalIgnoreCase)
        ? Thresholds.First(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)) : DefaultThreshold;
    public static int NormalizeTargets(int value) => Math.Clamp(value is 0 ? DefaultMaxTargets : value, 1, MaxTargetsLimit);
}
public sealed class DiagnosticObservation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Tool { get; set; } = "ZAP";
    public string RuleId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Confidence { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string SeverityDisplay => Severity switch
    { "0" => "情報", "1" => "低", "2" => "中", "3" => "高", _ => string.IsNullOrEmpty(Severity) ? "未指定" : Severity };
    [System.Text.Json.Serialization.JsonIgnore] public string ConfidenceDisplay => Confidence switch
    { "0" => "誤検知", "1" => "低", "2" => "中", "3" => "高", "4" => "確実（ZAP評価）", _ => string.IsNullOrEmpty(Confidence) ? "未指定" : Confidence };
    public string Url { get; set; } = "";
    public string Method { get; set; } = "";
    public string Parameter { get; set; } = "";
    public string Role { get; set; } = "";
    public DiagnosisState State { get; set; } = DiagnosisState.TargetAlertEffectUnconfirmed;
    [System.Text.Json.Serialization.JsonIgnore] public string StateDisplay => "要確認";
    public EvidenceReference Evidence { get; set; } = new("", "", "", "", "local-only");
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;
}
