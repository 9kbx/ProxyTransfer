using System.Net;
using System.Net.Sockets;

namespace ProxyTransfer.Api;

internal sealed class ListenPortAllocator
{
    private readonly ProxyTunnelHostOptions _options;

    public ListenPortAllocator(ProxyTunnelHostOptions options)
    {
        _options = options;
        ValidateRange();
    }

    public int ResolvePort(
        string listenAddress,
        int requestedPort,
        IReadOnlyCollection<int> occupiedPorts
    )
    {
        if (!IPAddress.TryParse(listenAddress, out var ipAddress))
        {
            throw new FormatException($"监听地址无效: {listenAddress}");
        }

        if (!TryGetRange(out var rangeStart, out var rangeEnd))
        {
            return requestedPort == -1 ? 0 : requestedPort;
        }

        if (requestedPort > 0)
        {
            if (requestedPort < rangeStart || requestedPort > rangeEnd)
            {
                throw new InvalidOperationException(
                    $"监听端口 {requestedPort} 不在允许范围内。当前允许范围: {rangeStart}-{rangeEnd}。"
                );
            }

            if (occupiedPorts.Contains(requestedPort) || !CanBind(ipAddress, requestedPort))
            {
                throw new InvalidOperationException(
                    $"监听端口 {requestedPort} 已被占用，无法启动。"
                );
            }

            return requestedPort;
        }

        if (requestedPort == 0)
        {
            return 0;
        }

        if (requestedPort != -1)
        {
            throw new InvalidOperationException(
                $"监听端口 {requestedPort} 无效。支持的值为正整数、0 或 -1。"
            );
        }

        var candidates = new List<int>();

        for (var port = rangeStart; port <= rangeEnd; port++)
        {
            if (occupiedPorts.Contains(port))
            {
                continue;
            }

            if (CanBind(ipAddress, port))
            {
                candidates.Add(port);
            }
        }

        if (candidates.Count > 0)
        {
            return candidates[Random.Shared.Next(candidates.Count)];
        }

        throw new InvalidOperationException(
            $"允许范围 {rangeStart}-{rangeEnd} 内没有可用监听端口。"
        );
    }

    private void ValidateRange()
    {
        if (_options.ListenPortRangeStart.HasValue != _options.ListenPortRangeEnd.HasValue)
        {
            throw new InvalidOperationException(
                "监听端口范围配置不完整，必须同时提供 ListenPortRangeStart 和 ListenPortRangeEnd。"
            );
        }

        if (!TryGetRange(out var rangeStart, out var rangeEnd))
        {
            return;
        }

        if (rangeStart <= 0 || rangeEnd <= 0 || rangeStart > 65535 || rangeEnd > 65535)
        {
            throw new InvalidOperationException("监听端口范围必须是 1-65535 之间的有效端口。");
        }

        if (rangeStart > rangeEnd)
        {
            throw new InvalidOperationException("监听端口范围起始值不能大于结束值。");
        }
    }

    private bool TryGetRange(out int rangeStart, out int rangeEnd)
    {
        if (_options.ListenPortRangeStart is int start && _options.ListenPortRangeEnd is int end)
        {
            rangeStart = start;
            rangeEnd = end;
            return true;
        }

        rangeStart = 0;
        rangeEnd = 0;
        return false;
    }

    private static bool CanBind(IPAddress listenAddress, int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(listenAddress, port);
            listener.Server.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                false
            );
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }
}
