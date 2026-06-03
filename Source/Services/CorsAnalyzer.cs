using PayloadPanda.Models;

namespace PayloadPanda.Services;

/// <summary>
/// Evaluates a response against the browser CORS rules and explains, in plain terms,
/// whether the request would be allowed and what (if anything) is missing.
/// Pure and side-effect free so it can be unit tested in isolation.
/// </summary>
public static class CorsAnalyzer
{
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
        var hasAllowOrigin = TryGetHeader(response, "Access-Control-Allow-Origin", out var allowOrigin);
        if (!hasAllowOrigin)
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
            var matchesOrigin = string.IsNullOrEmpty(origin)
                || isWildcard
                || allowOrigin.Trim().Equals(origin, StringComparison.OrdinalIgnoreCase);

            checks.Add(new CorsCheck
            {
                Label = "Access-Control-Allow-Origin",
                Status = matchesOrigin ? CorsCheckStatus.Pass : CorsCheckStatus.Fail,
                Detail = matchesOrigin
                    ? allowOrigin
                    : $"\"{allowOrigin}\" does not match the Origin \"{origin}\"."
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
            var hasAllowCreds = TryGetHeader(response, "Access-Control-Allow-Credentials", out var allowCreds);
            var credsOk = hasAllowCreds && allowCreds.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            checks.Add(new CorsCheck
            {
                Label = "Access-Control-Allow-Credentials",
                Status = credsOk ? CorsCheckStatus.Pass : CorsCheckStatus.Fail,
                Detail = credsOk
                    ? "true"
                    : hasAllowCreds
                        ? $"\"{allowCreds}\" — must be \"true\" for a credentialed request."
                        : "missing — required to be \"true\" for a credentialed request."
            });
        }

        if (isPreflight)
        {
            // Access-Control-Allow-Methods must include the requested method.
            if (!string.IsNullOrEmpty(method))
            {
                var hasAllowMethods = TryGetHeader(response, "Access-Control-Allow-Methods", out var allowMethods);
                var methodOk = hasAllowMethods && ListAllows(allowMethods, method);
                checks.Add(new CorsCheck
                {
                    Label = "Access-Control-Allow-Methods",
                    Status = methodOk ? CorsCheckStatus.Pass : CorsCheckStatus.Fail,
                    Detail = !hasAllowMethods
                        ? $"missing — does not permit {method}."
                        : methodOk
                            ? allowMethods
                            : $"\"{allowMethods}\" does not include {method}."
                });
            }

            // Access-Control-Allow-Headers must include each requested header.
            var requested = SplitList(requestedHeaders);
            if (requested.Count > 0)
            {
                var hasAllowHeaders = TryGetHeader(response, "Access-Control-Allow-Headers", out var allowHeaders);
                var missing = requested.Where(h => !(hasAllowHeaders && ListAllows(allowHeaders, h))).ToList();
                checks.Add(new CorsCheck
                {
                    Label = "Access-Control-Allow-Headers",
                    Status = missing.Count == 0 ? CorsCheckStatus.Pass : CorsCheckStatus.Fail,
                    Detail = missing.Count == 0
                        ? (hasAllowHeaders ? allowHeaders : "all requested headers allowed")
                        : $"not allowed: {string.Join(", ", missing)}."
                });
            }

            // Access-Control-Max-Age (informational).
            if (TryGetHeader(response, "Access-Control-Max-Age", out var maxAge))
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

    private static bool ListAllows(string headerValue, string token)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return false;
        if (headerValue.Trim() == "*")
            return true;
        return SplitList(headerValue).Any(v => v.Equals(token.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> SplitList(string value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static bool TryGetHeader(ResponseModel response, string name, out string value)
    {
        foreach (var header in response.Headers)
        {
            if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = header.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
