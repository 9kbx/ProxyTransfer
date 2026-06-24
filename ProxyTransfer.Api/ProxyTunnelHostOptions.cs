namespace ProxyTransfer.Api;

public sealed class ProxyTunnelHostOptions
{
    public string ListenAddress { get; set; } = "0.0.0.0";

    public string PublicHost { get; set; } = "127.0.0.1";

    public string ApiUrl { get; set; } = "http://0.0.0.0:5080";
}
