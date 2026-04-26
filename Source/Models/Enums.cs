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
