using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Capabilities;

internal interface IFilmExternalPlayer : IAsyncDisposable
{
    bool Running { get; }
    double Position { get; }
    double Duration { get; }
    bool Paused { get; }
    event EventHandler? Changed;
    event EventHandler<string>? Terminal;
    Task PauseAsync(bool paused);
    Task SeekAsync(double position);
    Task RateAsync(double rate);
    Task AspectAsync(string? aspect);
    Task VolumeAsync(int volume);
}

/// <summary>
/// Explicitly selected local mpv process. Only a trusted session receipt is sent,
/// over the private process-bound IPC pipe, never a shell/URL browser or upstream headers.
/// The Film owner retains the live native resource while its output is paused.
/// </summary>
internal sealed class FilmExternalPlayer : IFilmExternalPlayer
{
    private readonly Process process;
    private readonly NamedPipeClientStream pipe;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationTokenRegistration revoke;
    private readonly Task exit, stdout, stderr;
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly object sync = new();
    private readonly TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource output = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<JsonElement>? response;
    private Task? reader, polling, disposal;
    private long requestId;
    private long controlRevision;
    private volatile bool stopping, terminal;
    private bool optionalResponse;
    public bool Running => !stopping && !terminal && !exit.IsCompleted;
    public double Position { get; private set; }
    public double Duration { get; private set; }
    public bool Paused { get; private set; }
    public event EventHandler? Changed;
    public event EventHandler<string>? Terminal;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    private FilmExternalPlayer(Process process, NamedPipeClientStream pipe, CancellationToken token)
    {
        this.process = process; this.pipe = pipe;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        exit = process.WaitForExitAsync();
        stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        revoke = lifetime.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* Dispose reports a failed physical exit rather than a fake receipt. */ }
        });
    }

    internal static async Task<IFilmExternalPlayer> StartAsync(string executable, FilmMediaReceipt receipt,
        int volume, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            !Path.GetFileName(executable).Equals("mpv.exe", StringComparison.OrdinalIgnoreCase) ||
            receipt.Drm.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new FilmPlaybackException("EXTERNAL_PLAYER_UNAVAILABLE");
        token.ThrowIfCancellationRequested();
        string pipeName = "meowfilm-film-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        // URLs and keys never become command-line arguments or diagnostic output.
        foreach (string argument in new[] { "--no-config", "--idle=yes", "--force-window=yes", "--terminal=no",
            "--msg-level=all=no", "--volume=0", @"--input-ipc-server=\\.\pipe\" + pipeName })
            start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new FilmPlaybackException("EXTERNAL_PLAYER_UNAVAILABLE");
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var player = new FilmExternalPlayer(process, pipe, token);
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(token);
            opening.CancelAfter(TimeSpan.FromSeconds(12)); // Local process/first-output resource deadline, not a data TTL.
            await pipe.ConnectAsync(opening.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || server != (uint)process.Id)
                throw new FilmPlaybackException("EXTERNAL_PLAYER_UNAVAILABLE");
            player.reader = player.ReadAsync();
            await player.CallAsync(["set_property", "pause", true], opening.Token).ConfigureAwait(false);
            await player.CallAsync(["loadfile", receipt.Address.AbsoluteUri], opening.Token).ConfigureAwait(false);
            await player.loaded.Task.WaitAsync(opening.Token).ConfigureAwait(false);
            if (receipt.Position > 0) await player.CallAsync(["seek", receipt.Position, "absolute+exact"], opening.Token).ConfigureAwait(false);
            await player.CallAsync(["set_property", "pause", false], opening.Token).ConfigureAwait(false);
            await player.output.Task.WaitAsync(opening.Token).ConfigureAwait(false);
            var video = await player.CallAsync(["get_property", "video-out-params"], opening.Token).ConfigureAwait(false);
            if (FilmJson.Integer(video, "w") is not > 0 || FilmJson.Integer(video, "h") is not > 0)
                throw new FilmPlaybackException("EXTERNAL_PLAYER_FAILED");
            await player.PauseAsync(receipt.Paused).ConfigureAwait(false);
            await player.VolumeAsync(volume).ConfigureAwait(false);
            player.polling = player.PollAsync();
            _ = player.WatchExitAsync();
            return player;
        }
        catch
        {
            await player.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<JsonElement> CallAsync(object[] command, CancellationToken token = default, bool optional = false)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        wait.CancelAfter(TimeSpan.FromSeconds(5)); // One local IPC command must settle.
        await commands.WaitAsync(wait.Token).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            long id;
            lock (sync) { id = ++requestId; response = completion; optionalResponse = optional; }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { command, request_id = id });
            await pipe.WriteAsync(bytes, wait.Token).ConfigureAwait(false);
            await pipe.WriteAsync("\n"u8.ToArray(), wait.Token).ConfigureAwait(false);
            await pipe.FlushAsync(wait.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(wait.Token).ConfigureAwait(false);
        }
        finally { lock (sync) response = null; commands.Release(); }
    }

    private async Task ReadAsync()
    {
        // A bounded local IPC record, not a media/JSON business-page limit.
        byte[] buffer = new byte[4096];
        using var line = new MemoryStream();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                int count = await pipe.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false);
                if (count == 0) break;
                for (int index = 0; index < count; index++)
                {
                    byte value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        using var document = JsonDocument.Parse(line.ToArray());
                        Receive(document.RootElement);
                        line.SetLength(0);
                    }
                    else
                    {
                        if (line.Length == 65536) throw new FilmPlaybackException("EXTERNAL_PLAYER_FAILED");
                        line.WriteByte(value);
                    }
                }
            }
            if (!lifetime.IsCancellationRequested && !stopping && !terminal) Finish("media.failed");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { if (!stopping) Finish("media.failed"); }
        finally
        {
            var error = new FilmPlaybackException("EXTERNAL_PLAYER_FAILED");
            loaded.TrySetException(error); output.TrySetException(error);
            lock (sync) response?.TrySetException(error);
            _ = loaded.Task.Exception; _ = output.Task.Exception;
        }
    }
    private void Receive(JsonElement value)
    {
        var id = FilmJson.Member(value, "request_id");
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long number))
        {
            lock (sync)
            {
                if (number == requestId && response is { } waiting)
                {
                    string error = FilmJson.Text(value, "error");
                    if (optionalResponse && error is ("property unavailable" or "property not found"))
                        waiting.TrySetResult(JsonSerializer.SerializeToElement<object?>(null));
                    else if (error != "success") waiting.TrySetException(new FilmPlaybackException("EXTERNAL_PLAYER_FAILED"));
                    else waiting.TrySetResult(FilmJson.Member(value, "data").ValueKind == JsonValueKind.Undefined
                        ? FilmJson.Encode(new { }) : FilmJson.Member(value, "data").Clone());
                }
            }
        }
        switch (FilmJson.Text(value, "event"))
        {
            case "file-loaded": loaded.TrySetResult(); break;
            case "playback-restart": output.TrySetResult(); break;
            case "end-file":
                string reason = FilmJson.Text(value, "reason");
                if (reason == "eof") Finish("media.ended");
                else if (reason == "error") Finish("media.failed");
                break;
        }
    }
    private async Task PollAsync()
    {
        try
        {
            while (Running && !terminal)
            {
                long revision;
                lock (sync) revision = controlRevision;
                double position = OptionalNumber(await CallAsync(["get_property", "time-pos"], optional: true).ConfigureAwait(false));
                double duration = OptionalNumber(await CallAsync(["get_property", "duration"], optional: true).ConfigureAwait(false));
                var pause = await CallAsync(["get_property", "pause"]).ConfigureAwait(false);
                if (pause.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FilmPlaybackException("INVALID_RESPONSE");
                lock (sync)
                    if (revision == controlRevision)
                    { Position = position; Duration = duration; Paused = pause.GetBoolean(); }
                Changed?.Invoke(this, EventArgs.Empty);
                await Task.Delay(1000, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { if (!stopping && !terminal) Finish("media.failed"); }
    }
    private async Task WatchExitAsync()
    {
        await exit.ConfigureAwait(false);
        if (!stopping && !terminal) Finish("stop");
    }
    private void Finish(string action)
    {
        lock (sync)
        {
            if (stopping || terminal) return;
            terminal = true;
        }
        Terminal?.Invoke(this, action);
    }
    private static double OptionalNumber(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
            throw new FilmPlaybackException("INVALID_RESPONSE");
        return Math.Max(0, number);
    }
    public async Task PauseAsync(bool paused)
    {
        lock (sync) controlRevision++;
        await CallAsync(["set_property", "pause", paused]).ConfigureAwait(false);
        double? position = paused ? OptionalNumber(await CallAsync(["get_property", "time-pos"], optional: true).ConfigureAwait(false)) : null;
        lock (sync)
        {
            controlRevision++;
            Paused = paused;
            if (position is { } value) Position = value;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task SeekAsync(double position)
    {
        lock (sync) controlRevision++;
        await CallAsync(["seek", position, "absolute+exact"]).ConfigureAwait(false);
        lock (sync) { controlRevision++; Position = position; }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task RateAsync(double rate) => _ = await CallAsync(["set_property", "speed", rate]).ConfigureAwait(false);
    public async Task AspectAsync(string? aspect) => _ = await CallAsync(["set_property", "video-aspect-override",
        aspect is null ? -1d : aspect == "16:9" ? 16d / 9 : aspect == "4:3" ? 4d / 3 : 1d]).ConfigureAwait(false);
    public async Task VolumeAsync(int volume) => _ = await CallAsync(["set_property", "volume", volume]).ConfigureAwait(false);

    public ValueTask DisposeAsync()
    {
        lock (sync) { stopping = true; return new(disposal ??= DisposeCoreAsync()); }
    }
    private async Task DisposeCoreAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await lifetime.CancelAsync().ConfigureAwait(false);
        pipe.Dispose();
        await exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr, reader ?? Task.CompletedTask, polling ?? Task.CompletedTask).ConfigureAwait(false);
        revoke.Dispose(); lifetime.Dispose(); process.Dispose();
    }
}
