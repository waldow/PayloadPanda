namespace PayloadPanda.Models;

/// <summary>
/// Everything <c>HttpClient</c> normally hides about a request: DNS resolution,
/// the TCP connection, the TLS handshake and certificate chain, the exact bytes
/// sent, and a per-phase timing breakdown. Produced by <c>RawSocketService</c>.
/// </summary>
public class ConnectionDiagnostics
{
    public string Scheme { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }

    public List<string> ResolvedIpAddresses { get; set; } = [];
    public string ChosenIpAddress { get; set; } = string.Empty;
    public string? LocalEndpoint { get; set; }
    public string? RemoteEndpoint { get; set; }

    public TimingBreakdown Timings { get; set; } = new();

    // TLS — left null for plain http.
    public string? TlsProtocol { get; set; }
    public string? CipherSuite { get; set; }
    public bool TlsHandshakeSucceeded { get; set; }
    public string? SslPolicyErrors { get; set; }
    public List<CertInfo> Certificates { get; set; } = [];

    public string RawRequest { get; set; } = string.Empty;
    public string RawResponseHead { get; set; } = string.Empty;

    // Set only when a phase failed; otherwise the connection completed.
    public RequestPhase? FailedPhase { get; set; }
    public string? ErrorMessage { get; set; }

    // --- Convenience accessors for binding ---
    public bool IsSecure => !string.IsNullOrEmpty(TlsProtocol);
    public string ResolvedIpSummary => ResolvedIpAddresses.Count == 0 ? "—" : string.Join(", ", ResolvedIpAddresses);
    public bool CertificatesPresent => Certificates.Count > 0;
    public bool HasError => FailedPhase.HasValue;
}

public class TimingBreakdown
{
    public double DnsMs { get; set; }
    public double TcpConnectMs { get; set; }
    public double TlsHandshakeMs { get; set; }
    public double TimeToFirstByteMs { get; set; }
    public double TotalMs { get; set; }
}

public class CertInfo
{
    public string Subject { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public DateTime NotBefore { get; set; }
    public DateTime NotAfter { get; set; }
    public string Thumbprint { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string SignatureAlgorithm { get; set; } = string.Empty;
    public List<string> SubjectAlternativeNames { get; set; } = [];
    public int ChainIndex { get; set; }

    public bool IsExpired => DateTime.Now > NotAfter || DateTime.Now < NotBefore;
    public bool IsExpiringSoon => !IsExpired && DateTime.Now > NotAfter.AddDays(-30);

    // Strings consumed directly by the UI (DiagnosticBrushConverter maps Status to a color).
    public string Status => IsExpired ? "Expired" : IsExpiringSoon ? "Expiring Soon" : "Valid";
    public string Role => ChainIndex == 0 ? "Leaf" : "Chain";
    public string SansSummary => SubjectAlternativeNames.Count == 0 ? "—" : string.Join(", ", SubjectAlternativeNames);
}

/// <summary>One bar in the timing waterfall. Built by the view-model from <see cref="TimingBreakdown"/>.</summary>
public class TimingPhaseRow
{
    public string Label { get; set; } = string.Empty;
    public double Milliseconds { get; set; }
    public double ScaleMax { get; set; } = 1;
    public string MsDisplay => $"{Milliseconds:F0} ms";
}
