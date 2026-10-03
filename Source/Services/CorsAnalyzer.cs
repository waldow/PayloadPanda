using PayloadPanda.Models;

namespace PayloadPanda.Services;

/// <summary>
/// Evaluates a response against the browser CORS rules (the Fetch standard's CORS check
/// and CORS-preflight fetch) and explains, in plain terms, whether the request would be
/// allowed and what (if anything) is missing.
/// Pure and side-effect free so it can be unit tested in isolation.
/// </summary>
public static class CorsAnalyzer
{
    // Methods a browser never needs Access-Control-Allow-Methods to permit.
    private static readonly string[] SafelistedMethods = ["GET", "HEAD", "POST"];

    public static CorsAnalysisResult Analyze(
        string origin,
        string method,
        string requestedHeaders,
        bool includeCredentials,
        bool isPreflight,
        ResponseModel response)
    {
        var result = new CorsAnalysisResult();
        var checks = result.Checks;

        origin = (origin ?? string.Empty).Trim();
        method = (method ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(origin))
        {
            checks.Add(new CorsCheck
            {
                Label = "Origin",
                Status = CorsCheckStatus.Fail,
                Detail = "missing - a browser CORS request must send an Origin header."
            });
        }

        // Preflight responses are expected to be a 2xx.
        if (isPreflight)
        {
            checks.Add(new CorsCheck
            {
                Label = "Preflight status",
                Status = response.StatusCode is >= 200 and < 300 ? CorsCheckStatus.Pass : CorsCheckStatus.Fail,
                Detail = $"{response.StatusCode} {response.ReasonPhrase}".Trim()
            });
        }

        // Access-Control-Allow-Origin
        var allowOrigin = response.GetHeader("Access-Control-Allow-Origin");
        if (allowOrigin is null)
        {
            checks.Add(new CorsCheck
            {
                Label = "Access-Control-Allow-Origin",
                Status = CorsCheckStatus.Fail,
                Detail = "missing — the server did not return this header, so the browser will block the response."
            });
        }
        else
        {
            var isWildcard = allowOrigin.Trim() == "*";
            // Browsers compare the serialized origin byte for byte — case included.
            var matchesOrigin = isWildcard || allowOrigin.Trim().Equals(origin, StringComparison.Ordinal);

            checks.Add(new CorsCheck
            {
                Label = "Access-Control-Allow-Origin",
                Status = matchesOrigin ? CorsCheckStatus.Pass : CorsCheckStatus.Fail,
                Detail = matchesOrigin
                    ? allowOrigin
                    : $"\"{allowOrigin}\" does not exactly match the Origin \"{origin}\" (the comparison is case-sensitive)."
            });

            // Wildcard + credentials is rejected by browsers.
            if (includeCredentials && isWildcard)
            {
                checks.Add(new CorsCheck
                {
                    Label = "Credentials + wildcard",
                    Status = CorsCheckStatus.Fail,
                    Detail = "Allow-Origin \"*\" cannot be used with credentials — the server must echo the specific Origin."
                });
            }
        }

        // Access-Control-Allow-Credentials
        if (includeCredentials)
        {
            var allowCreds = response.GetHeader("Access-Control-Allow-Credentials");
            var credsOk = allowCreds?.Trim() == "true";
            checks.Add(new CorsCheck
            {
                Label = "Access-Control-Allow-Credentials",
                Status = credsOk ? CorsCheckStatus.Pass : CorsCheckStatus.Fail,
                Detail = credsOk
                    ? "true"
                    : allowCreds is not null
                        ? $"\"{allowCreds}\" — must be exactly \"true\" for a credentialed request."
                        : "missing — required to be \"true\" for a credentialed request."
            });
        }

        if (isPreflight)
        {
            if (!string.IsNullOrEmpty(method))
                checks.Add(CheckMethod(method, response.GetHeader("Access-Control-Allow-Methods"), includeCredentials));

            var requested = SplitList(requestedHeaders);
            if (requested.Count > 0)
                checks.Add(CheckHeaders(requested, response.GetHeader("Access-Control-Allow-Headers"), includeCredentials));

            // Access-Control-Max-Age (informational).
            if (response.GetHeader("Access-Control-Max-Age") is { } maxAge)
            {
                checks.Add(new CorsCheck
                {
                    Label = "Access-Control-Max-Age",
                    Status = CorsCheckStatus.Pass,
                    Detail = $"{maxAge}s — preflight result is cached this long."
                });
            }
        }

        result.Passed = checks.All(c => c.Status != CorsCheckStatus.Fail);
        var failCount = checks.Count(c => c.Status == CorsCheckStatus.Fail);
        result.Summary = result.Passed
            ? (isPreflight ? "Preflight PASSES — the request would be allowed." : "CORS PASSES — the response is readable by the browser.")
            : $"CORS FAILS — {failCount} blocking issue{(failCount == 1 ? "" : "s")}.";

        return result;
    }

    // A method passes if it's listed, if it's CORS-safelisted (GET/HEAD/POST), or if the
    // list is "*" — which counts as a wildcard only for requests without credentials.
    private static CorsCheck CheckMethod(string method, string? allowMethods, bool includeCredentials)
    {
        const string label = "Access-Control-Allow-Methods";
        var listed = SplitList(allowMethods);

        if (listed.Contains(method, StringComparer.OrdinalIgnoreCase))
            return new CorsCheck { Label = label, Status = CorsCheckStatus.Pass, Detail = allowMethods! };

        if (SafelistedMethods.Contains(method, StringComparer.OrdinalIgnoreCase))
        {
            return new CorsCheck
            {
                Label = label,
                Status = CorsCheckStatus.Pass,
                Detail = $"{method} is a CORS-safelisted method — it doesn't need to be listed."
            };
        }

        if (listed.Contains("*"))
        {
            return includeCredentials
                ? new CorsCheck
                {
                    Label = label,
                    Status = CorsCheckStatus.Fail,
                    Detail = $"\"*\" is treated literally for credentialed requests, so {method} must be listed explicitly."
                }
                : new CorsCheck { Label = label, Status = CorsCheckStatus.Pass, Detail = allowMethods! };
        }

        return new CorsCheck
        {
            Label = label,
            Status = CorsCheckStatus.Fail,
            Detail = allowMethods is null
                ? $"missing — does not permit {method}."
                : $"\"{allowMethods}\" does not include {method}."
        };
    }

    // Each requested header must be listed. "*" covers the rest only for requests without
    // credentials, and never covers Authorization, which must always be named explicitly.
    private static CorsCheck CheckHeaders(List<string> requested, string? allowHeaders, bool includeCredentials)
    {
        const string label = "Access-Control-Allow-Headers";
        var listed = SplitList(allowHeaders);
        var wildcard = listed.Contains("*") && !includeCredentials;

        var notAllowed = requested
            .Where(h => !listed.Contains(h, StringComparer.OrdinalIgnoreCase))
            .Where(h => !wildcard || h.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (notAllowed.Count == 0)
        {
            return new CorsCheck
            {
                Label = label,
                Status = CorsCheckStatus.Pass,
                Detail = allowHeaders ?? "all requested headers allowed"
            };
        }

        var reasons = new List<string> { $"not allowed: {string.Join(", ", notAllowed)}." };
        if (listed.Contains("*"))
        {
            reasons.Add(includeCredentials
                ? "\"*\" is treated literally for credentialed requests."
                : "\"*\" never covers Authorization — it must be listed explicitly.");
        }

        return new CorsCheck { Label = label, Status = CorsCheckStatus.Fail, Detail = string.Join(" ", reasons) };
    }

    private static List<string> SplitList(string? value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
