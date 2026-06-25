using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ProxyTransfer.TunnelHost;

public sealed class TunnelDefinitionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _stateFilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TunnelDefinitionStore(IOptions<TunnelHostOptions> options)
    {
        _stateFilePath = Path.GetFullPath(options.Value.StateFilePath);
    }

    public async Task<TunnelHostState> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateFilePath))
        {
            return new TunnelHostState();
        }

        await using var stream = File.OpenRead(_stateFilePath);
        var state = await JsonSerializer
            .DeserializeAsync<TunnelHostState>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return state ?? new TunnelHostState();
    }

    public async Task SaveAsync(TunnelHostState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempFilePath = _stateFilePath + ".tmp";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var stream = File.Create(tempFilePath))
            {
                await JsonSerializer
                    .SerializeAsync(stream, state, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempFilePath, _stateFilePath, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
