namespace PayloadPanda.Models;

public enum HttpMethodType
{
    GET,
    POST,
    PUT,
    DELETE,
    PATCH,
    HEAD,
    OPTIONS
}

public enum BodyMode
{
    None,
    Raw,
    Json,
    Xml,
    FormUrlEncoded
}

public enum AuthMode
{
    None,
    Bearer,
    Basic,
    ApiKey
}

public enum RequestMode
{
    Http,
    RawSocket
}

public enum RequestPhase
{
    Dns,
    TcpConnect,
    TlsHandshake,
    SendRequest,
    ReadResponse
}

public enum CurlExportStyle
{
    Bash,       // curl, \ continuation, '\'' escaping (Linux / macOS / Git Bash)
    PowerShell, // curl.exe, ` continuation, '' escaping (Windows PowerShell)
    Cmd         // curl, ^ continuation, "..." with \" escaping (Windows cmd.exe)
}
