using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ProxyTransfer.Tunnel;

internal static class Socks5Protocol
{
    public static async Task EstablishUpstreamConnectionAsync(
        NetworkStream upstreamStream,
        ProxyEndpoint remoteProxy,
        string targetHost,
        int targetPort,
        CancellationToken token
    )
    {
        if (!remoteProxy.IsSocks5)
        {
            throw new InvalidOperationException($"上游不是 SOCKS5 代理: {remoteProxy.ProxyUri}");
        }

        var methods = remoteProxy.HasCredentials ? new byte[] { 0x00, 0x02 } : new byte[] { 0x00 };
        var handshakeRequest = new byte[2 + methods.Length];
        handshakeRequest[0] = 0x05;
        handshakeRequest[1] = (byte)methods.Length;
        Array.Copy(methods, 0, handshakeRequest, 2, methods.Length);

        await upstreamStream.WriteAsync(handshakeRequest, token).ConfigureAwait(false);

        var handshakeResponse = new byte[2];
        await upstreamStream.ReadExactlyAsync(handshakeResponse, token).ConfigureAwait(false);

        if (handshakeResponse[0] != 0x05)
        {
            throw new InvalidOperationException("SOCKS5 握手版本不正确。");
        }

        if (handshakeResponse[1] == 0x02)
        {
            if (!remoteProxy.HasCredentials)
            {
                throw new InvalidOperationException(
                    "SOCKS5 服务端要求用户名/密码认证，但当前未提供凭据。"
                );
            }

            await AuthenticateAsync(upstreamStream, remoteProxy, token).ConfigureAwait(false);
        }
        else if (handshakeResponse[1] == 0xFF)
        {
            throw new InvalidOperationException("SOCKS5 服务端拒绝了全部认证方式。");
        }
        else if (handshakeResponse[1] != 0x00)
        {
            throw new InvalidOperationException(
                $"SOCKS5 服务端返回了不支持的认证方式: 0x{handshakeResponse[1]:X2}"
            );
        }

        await SendConnectCommandAsync(upstreamStream, targetHost, targetPort, token)
            .ConfigureAwait(false);
        await ValidateConnectResponseAsync(upstreamStream, token).ConfigureAwait(false);
    }

    public static async Task<bool> NegotiateNoAuthAsync(
        NetworkStream clientStream,
        CancellationToken token
    )
    {
        var greetingHeader = new byte[2];
        await clientStream.ReadExactlyAsync(greetingHeader, token).ConfigureAwait(false);

        if (greetingHeader[0] != 0x05)
        {
            throw new InvalidOperationException("客户端使用了非 SOCKS5 协议版本。");
        }

        var methodCount = greetingHeader[1];
        if (methodCount == 0)
        {
            throw new InvalidOperationException("客户端未提供任何 SOCKS5 认证方式。");
        }

        var methods = new byte[methodCount];
        await clientStream.ReadExactlyAsync(methods, token).ConfigureAwait(false);

        if (!methods.Contains((byte)0x00))
        {
            await clientStream.WriteAsync(new byte[] { 0x05, 0xFF }, token).ConfigureAwait(false);
            return false;
        }

        await clientStream.WriteAsync(new byte[] { 0x05, 0x00 }, token).ConfigureAwait(false);
        return true;
    }

    public static async Task<(string Host, int Port)> ReadConnectRequestAsync(
        NetworkStream clientStream,
        CancellationToken token
    )
    {
        var requestHeader = new byte[4];
        await clientStream.ReadExactlyAsync(requestHeader, token).ConfigureAwait(false);

        if (requestHeader[0] != 0x05)
        {
            throw new InvalidOperationException("客户端 CONNECT 请求的 SOCKS5 版本不正确。");
        }

        if (requestHeader[1] != 0x01)
        {
            throw new InvalidOperationException(
                $"当前仅支持 SOCKS5 CONNECT 命令，收到: 0x{requestHeader[1]:X2}"
            );
        }

        var host = await ReadHostAsync(clientStream, requestHeader[3], token).ConfigureAwait(false);

        var portBuffer = new byte[2];
        await clientStream.ReadExactlyAsync(portBuffer, token).ConfigureAwait(false);
        var port = (portBuffer[0] << 8) | portBuffer[1];

        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new InvalidOperationException($"客户端请求了无效端口: {port}");
        }

        return (host, port);
    }

    public static Task WriteSuccessResponseAsync(
        NetworkStream clientStream,
        CancellationToken token
    )
    {
        return clientStream
            .WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, token)
            .AsTask();
    }

    public static Task WriteFailureResponseAsync(
        NetworkStream clientStream,
        byte replyCode,
        CancellationToken token
    )
    {
        return clientStream
            .WriteAsync(new byte[] { 0x05, replyCode, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, token)
            .AsTask();
    }

    private static async Task AuthenticateAsync(
        NetworkStream upstreamStream,
        ProxyEndpoint remoteProxy,
        CancellationToken token
    )
    {
        var userBytes = Encoding.UTF8.GetBytes(remoteProxy.UserName ?? string.Empty);
        var passwordBytes = Encoding.UTF8.GetBytes(remoteProxy.Password ?? string.Empty);
        var authRequest = new byte[3 + userBytes.Length + passwordBytes.Length];
        authRequest[0] = 0x01;
        authRequest[1] = (byte)userBytes.Length;
        Array.Copy(userBytes, 0, authRequest, 2, userBytes.Length);
        authRequest[2 + userBytes.Length] = (byte)passwordBytes.Length;
        Array.Copy(passwordBytes, 0, authRequest, 3 + userBytes.Length, passwordBytes.Length);

        await upstreamStream.WriteAsync(authRequest, token).ConfigureAwait(false);

        var authResponse = new byte[2];
        await upstreamStream.ReadExactlyAsync(authResponse, token).ConfigureAwait(false);
        if (authResponse[1] != 0x00)
        {
            throw new InvalidOperationException("SOCKS5 用户名/密码认证失败。");
        }
    }

    private static async Task SendConnectCommandAsync(
        NetworkStream socksStream,
        string targetHost,
        int targetPort,
        CancellationToken token
    )
    {
        if (targetPort is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPort), "目标端口超出有效范围。");
        }

        byte[] addressBytes;
        byte addressType;

        if (IPAddress.TryParse(targetHost, out var ipAddress))
        {
            addressBytes = ipAddress.GetAddressBytes();
            addressType = ipAddress.AddressFamily switch
            {
                AddressFamily.InterNetwork => 0x01,
                AddressFamily.InterNetworkV6 => 0x04,
                _ => throw new InvalidOperationException($"不支持的 IP 地址类型: {targetHost}"),
            };
        }
        else
        {
            addressBytes = Encoding.ASCII.GetBytes(targetHost);
            if (addressBytes.Length is 0 or > 255)
            {
                throw new InvalidOperationException($"目标域名长度无效: {targetHost}");
            }

            addressType = 0x03;
        }

        var commandLength = 6 + addressBytes.Length + (addressType == 0x03 ? 1 : 0);
        var command = new byte[commandLength];
        command[0] = 0x05;
        command[1] = 0x01;
        command[2] = 0x00;
        command[3] = addressType;

        var offset = 4;
        if (addressType == 0x03)
        {
            command[offset++] = (byte)addressBytes.Length;
        }

        Array.Copy(addressBytes, 0, command, offset, addressBytes.Length);
        offset += addressBytes.Length;
        command[offset++] = (byte)(targetPort >> 8);
        command[offset] = (byte)targetPort;

        await socksStream.WriteAsync(command, token).ConfigureAwait(false);
    }

    private static async Task ValidateConnectResponseAsync(
        NetworkStream socksStream,
        CancellationToken token
    )
    {
        var fixedResponse = new byte[4];
        await socksStream.ReadExactlyAsync(fixedResponse, token).ConfigureAwait(false);

        if (fixedResponse[1] != 0x00)
        {
            throw new InvalidOperationException(
                $"SOCKS5 CONNECT 失败，错误码: 0x{fixedResponse[1]:X2}"
            );
        }

        var addressLength = fixedResponse[3] switch
        {
            0x01 => 4,
            0x03 => await ReadAddressLengthAsync(socksStream, token).ConfigureAwait(false),
            0x04 => 16,
            _ => throw new InvalidOperationException(
                $"未知的 SOCKS5 地址类型: 0x{fixedResponse[3]:X2}"
            ),
        };

        var trailingBytes = new byte[addressLength + 2];
        await socksStream.ReadExactlyAsync(trailingBytes, token).ConfigureAwait(false);
    }

    private static async Task<string> ReadHostAsync(
        NetworkStream stream,
        byte addressType,
        CancellationToken token
    )
    {
        return addressType switch
        {
            0x01 => await ReadIpAddressAsync(stream, 4, token).ConfigureAwait(false),
            0x03 => await ReadDomainAsync(stream, token).ConfigureAwait(false),
            0x04 => await ReadIpAddressAsync(stream, 16, token).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"客户端请求使用了未知地址类型: 0x{addressType:X2}"
            ),
        };
    }

    private static async Task<string> ReadIpAddressAsync(
        NetworkStream stream,
        int length,
        CancellationToken token
    )
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
        return new IPAddress(buffer).ToString();
    }

    private static async Task<string> ReadDomainAsync(NetworkStream stream, CancellationToken token)
    {
        var lengthBuffer = new byte[1];
        await stream.ReadExactlyAsync(lengthBuffer, token).ConfigureAwait(false);

        var domainLength = lengthBuffer[0];
        if (domainLength == 0)
        {
            throw new InvalidOperationException("客户端请求中的域名长度不能为 0。");
        }

        var domainBytes = new byte[domainLength];
        await stream.ReadExactlyAsync(domainBytes, token).ConfigureAwait(false);
        return Encoding.ASCII.GetString(domainBytes);
    }

    private static async Task<int> ReadAddressLengthAsync(
        NetworkStream socksStream,
        CancellationToken token
    )
    {
        var lengthBuffer = new byte[1];
        await socksStream.ReadExactlyAsync(lengthBuffer, token).ConfigureAwait(false);
        return lengthBuffer[0];
    }
}
