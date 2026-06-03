namespace PayloadPanda.Models;

public enum CorsCheckStatus
{
    Pass,
    Warn,
    Fail
}

public class CorsCheck
{
    public string Label { get; set; } = string.Empty;
    public CorsCheckStatus Status { get; set; } = CorsCheckStatus.Pass;
    public string Detail { get; set; } = string.Empty;
}

public class CorsAnalysisResult
{
    public bool Passed { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<CorsCheck> Checks { get; set; } = [];
}
