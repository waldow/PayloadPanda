using PayloadPanda.Models;

namespace PayloadPanda.Services;

/// <summary>
/// Thrown by <see cref="RawSocketService"/> when a connection phase fails. Carries
/// the partial <see cref="ConnectionDiagnostics"/> gathered up to the failure so the
/// UI can still show which phase broke and everything that happened before it.
/// </summary>
public class RawSocketException(string message, ConnectionDiagnostics diagnostics, Exception? inner = null)
    : Exception(message, inner)
{
    public ConnectionDiagnostics Diagnostics { get; } = diagnostics;
}
