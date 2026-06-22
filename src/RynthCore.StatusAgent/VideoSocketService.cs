using System.Diagnostics;
using System.Net.WebSockets;

namespace RynthCore.StatusAgent;

/// <summary>
/// HD video, take 2 — CLIENT-SERVER over a WebSocket (not WebRTC). The app opens ws://&lt;agent&gt;/video?pid=N
/// (routes over Tailscale exactly like the MJPEG/status HTTP, so it works REMOTELY with no ICE/NAT/mDNS),
/// and the agent streams H.264 which the app decodes natively (VideoToolbox / AVSampleBufferDisplayLayer)
/// — no level/profile limits, so true 60fps at native size.
///
/// Wire format: the raw H.264 Annex-B byte stream (AUD-delimited, SPS/PPS repeated in-band before each
/// IDR), forwarded as binary WebSocket messages exactly as ffmpeg emits it. The client reassembles the
/// byte stream and does all NAL/AU parsing + AVCC conversion (it needs an Annex-B parser anyway). Keeping
/// the agent a dumb pipe avoids fragile server-side NAL rewriting.
/// </summary>
internal sealed class VideoSocketService
{
    private readonly int _maxWidth, _fps, _bitrateKbps;

    public VideoSocketService(int maxWidth, int fps, int bitrateKbps)
    {
        _maxWidth = maxWidth <= 0 ? 0 : Math.Clamp(maxWidth, 320, 3840);
        _fps = Math.Clamp(fps, 10, 60);
        _bitrateKbps = Math.Clamp(bitrateKbps, 1000, 40000);
    }

    /// Stream H.264 to one connected WebSocket client until it closes or the window goes away.
    /// Capture is PrintWindow(PW_RENDERFULLCONTENT) — occlusion-proof + by-HWND, so it always captures
    /// THAT client's content regardless of position/z-order (ddagrab grabbed a screen region → the
    /// desktop when the window wasn't on top). ~15-20fps ceiling; WGC is the future 60fps upgrade.
    public async Task StreamAsync(WebSocket ws, int pid, CancellationToken ct)
    {
        var cap = VideoCapture.Open(pid, _maxWidth);
        if (cap is null)
        {
            AgentLog.Info($"[vsock] pid {pid}: no renderable window");
            try { await ws.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "no window", ct); } catch { }
            return;
        }

        // Short keyframe interval (~0.5s at the real PrintWindow rate) so the client's drop-to-live flush
        // resyncs almost instantly. Trades a little bitrate for control responsiveness — HD is the Wi-Fi/fps
        // tier anyway (Saver is the data-saver).
        int gop = Math.Max(6, _fps / 6);
        // PrintWindow frames piped in as raw BGR; libx264 (NVENC needs driver 570+). High profile is fine —
        // VideoToolbox has no level limit. Annex-B (AUD-delimited) on stdout. Low-latency flags: nobuffer +
        // no input probe (we declare the format) kill input buffering; max_delay 0 + flush_packets 1 stop the
        // muxer from holding packets, so each frame ships the instant it's encoded.
        string ffArgs =
            $"-hide_banner -loglevel error -fflags nobuffer -probesize 32 -analyzeduration 0 " +
            $"-f rawvideo -pixel_format bgr24 " +
            $"-video_size {cap.Width}x{cap.Height} -framerate {_fps} -use_wallclock_as_timestamps 1 -i - " +
            $"-an -pix_fmt yuv420p -c:v libx264 -profile:v high -preset ultrafast -tune zerolatency " +
            $"-b:v {_bitrateKbps}k -x264-params repeat-headers=1:bframes=0 -g {gop} -bf 0 " +
            $"-max_delay 0 -flush_packets 1 -bsf:v h264_metadata=aud=insert -f h264 -";

        Process? ff;
        try { ff = Process.Start(new ProcessStartInfo("ffmpeg", ffArgs)
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }); }
        catch (Exception ex) { AgentLog.Warn($"[vsock] ffmpeg start failed pid {pid}: {ex.Message}"); try { await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "encode failed", ct); } catch { } return; }
        if (ff is null) { try { await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "encode failed", ct); } catch { } return; }
        ff.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AgentLog.Debug($"[vsock ff {pid}] {e.Data}"); };
        ff.BeginErrorReadLine();
        AgentLog.Info($"[vsock] pid {pid} PrintWindow {cap.Width}x{cap.Height} @{_fps}fps H264/WebSocket");

        var capTask = Task.Run(() => CapturePump(cap, ff!.StandardInput.BaseStream, ct), ct);
        try
        {
            await PumpAsync(ff.StandardOutput.BaseStream, ws, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { AgentLog.Debug($"[vsock] pid {pid} pump ended: {ex.Message}"); }
        finally
        {
            try { ff.Kill(true); } catch { }
            try { ff.WaitForExit(1500); } catch { }
            try { ff.Dispose(); } catch { }
            try { await capTask.ConfigureAwait(false); } catch { }
            AgentLog.Info($"[vsock] pid {pid} stopped");
        }
    }

    // Grab PrintWindow frames at ~_fps and feed tightly-packed BGR24 into ffmpeg's stdin; close stdin on exit.
    private void CapturePump(VideoCapture cap, Stream ffStdin, CancellationToken ct)
    {
        int frameMs = Math.Max(1, 1000 / _fps);
        var sw = Stopwatch.StartNew();
        long next = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (cap.TryGrab(out byte[] bgr))
                {
                    try { ffStdin.Write(bgr, 0, bgr.Length); ffStdin.Flush(); } catch { break; }
                }
                next += frameMs;
                long wait = next - sw.ElapsedMilliseconds;
                if (wait > 1) Thread.Sleep((int)wait);
                else if (wait < -200) next = sw.ElapsedMilliseconds;
            }
        }
        catch { }
        try { ffStdin.Close(); } catch { }
    }

    // Dumb pipe: forward ffmpeg's raw Annex-B stdout to the WebSocket in chunks. The client parses it.
    private async Task PumpAsync(Stream stdout, WebSocket ws, CancellationToken ct)
    {
        var chunk = new byte[32768];
        int read;
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open &&
               (read = await stdout.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
        {
            await ws.SendAsync(chunk.AsMemory(0, read), WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
        }
    }
}
