using System.Net;

namespace ProxyTransfer.Tunnel;

public enum ProxyProtocol
{
    Socks5,
    Http,
}

public sealed record ProxyEndpoint(
    ProxyProtocol Protocol,
    string Host,
    int Port,
    string? UserName = null,
    string? Password = null
)
{
    public string ProxyUri =>
        $"{GetScheme(Protocol)}://{BuildUserInfo(UserName, Password)}{FormatHost(Host)}:{Port}";

    public string SafeDisplayUri =>
        string.IsNullOrWhiteSpace(UserName)
            ? $"{GetScheme(Protocol)}://{FormatHost(Host)}:{Port}"
            : $"{GetScheme(Protocol)}://{UserName}:***@{FormatHost(Host)}:{Port}";

    public bool HasCredentials => !string.IsNullOrWhiteSpace(UserName);

    public bool IsSocks5 => Protocol == ProxyProtocol.Socks5;

    public bool IsHttp => Protocol == ProxyProtocol.Http;

    public static ProxyEndpoint Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("代理字符串不能为空。", nameof(value));
        }

        var trimmed = value.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = $"http://{trimmed}";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new FormatException($"无效的代理格式: {value}");
        }

        ProxyProtocol protocol;
        if (string.Equals(uri.Scheme, "socks5", StringComparison.OrdinalIgnoreCase))
        {
            protocol = ProxyProtocol.Socks5;
        }
        else if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            protocol = ProxyProtocol.Http;
        }
        else
        {
            throw new FormatException($"仅支持 HTTP 或 SOCKS5 代理: {value}");
        }

        if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0 || uri.Port > IPEndPoint.MaxPort)
        {
            throw new FormatException($"代理必须包含有效的 Host 和 Port: {value}");
        }

        string? userName = null;
        string? password = null;
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            userName = Uri.UnescapeDataString(parts[0]);
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }

        return new ProxyEndpoint(protocol, uri.Host, uri.Port, userName, password);
    }

    private static string GetScheme(ProxyProtocol protocol) =>
        protocol == ProxyProtocol.Http ? Uri.UriSchemeHttp : "socks5";

    private static string BuildUserInfo(string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return string.Empty;
        }

        var escapedUserName = Uri.EscapeDataString(userName);
        var escapedPassword = password is null ? string.Empty : Uri.EscapeDataString(password);
        return $"{escapedUserName}:{escapedPassword}@";
    }

    private static string FormatHost(string host) =>
        host.Contains(':', StringComparison.Ordinal)
        && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
}
