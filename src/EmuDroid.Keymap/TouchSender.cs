using EmuDroid.Keymap.Grpc;
using global::Grpc.Net.Client;

namespace EmuDroid.Keymap;

/// <summary>Sends multi-touch events to one running emulator over its local gRPC port.</summary>
public sealed class TouchSender : ITouchSink, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly EmulatorController.EmulatorControllerClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TouchSender(int grpcPort, int screenWidth, int screenHeight)
    {
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{grpcPort}");
        _client = new EmulatorController.EmulatorControllerClient(_channel);
        ScreenWidth = screenWidth;
        ScreenHeight = screenHeight;
    }

    public int ScreenWidth { get; }
    public int ScreenHeight { get; }

    /// <summary>Finger <paramref name="id"/> down (or moved) at a normalized position.</summary>
    public Task DownAsync(int id, double nx, double ny) => SendAsync(id, nx, ny, pressure: 1);

    public Task UpAsync(int id, double nx, double ny) => SendAsync(id, nx, ny, pressure: 0);

    public async Task TapAsync(int id, double nx, double ny)
    {
        await DownAsync(id, nx, ny);
        await Task.Delay(40);
        await UpAsync(id, nx, ny);
    }

    private async Task SendAsync(int id, double nx, double ny, int pressure)
    {
        var touch = new Touch
        {
            X = (int)Math.Round(Math.Clamp(nx, 0, 1) * (ScreenWidth - 1)),
            Y = (int)Math.Round(Math.Clamp(ny, 0, 1) * (ScreenHeight - 1)),
            Identifier = id,
            Pressure = pressure,
            TouchMajor = pressure > 0 ? 8 : 0,
        };
        var e = new TouchEvent();
        e.Touches.Add(touch);

        await _gate.WaitAsync();
        try
        {
            await _client.sendTouchAsync(e, deadline: DateTime.UtcNow.AddSeconds(2));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _channel.Dispose();
        _gate.Dispose();
    }
}
