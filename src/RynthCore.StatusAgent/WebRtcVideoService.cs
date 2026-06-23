using System.Diagnostics;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RynthCore.StatusAgent;

/// <summary>
/// HD video mode: streams an AC client window to the app over WebRTC (H.264). The HD path uses ffmpeg
/// ddagrab (GPU Desktop Duplication) on the window's screen region — 60fps-class, captures D3D content,
/// best for the foreground "play" window (it's monitor-region capture, so occlusion-prone; the MJPEG
/// presets stay on occlusion-proof PrintWindow for background glancing). Out-of-process (the agent),
/// so the game is never touched. Opt-in: only constructed when AgentConfig.EnableVideoStream is set.
///
/// libx264 because the installed NVIDIA driver (560.94) predates ffmpeg's NVENC requirement (570+);
/// swapping to h264_nvenc (driver update) or a Media Foundation MFT is a drop-in encoder change later.
/// A short GOP gives fast recovery after loss; true on-demand keyframes (PLI) come with NVENC/MF.
/// </summary>
internal sealed class WebRtcVideoService : IDisposable
{
    private readonly int _maxWidth, _fps, _bitrateKbps;
    private readonly Dictionary<int, PidSession> _sessions = new();
    private readonly object _lock = new();
    private bool _disposed;

    public WebRtcVideoService(int maxWidth, int fps, int bitrateKbps)
    {
        _maxWidth = maxWidth <= 0 ? 0 : Math.Clamp(maxWidth, 320, 3840);   // 0 = native window size
        _fps = Math.Clamp(fps, 10, 60);
        _bitrateKbps = Math.Clamp(bitrateKbps, 1000, 40000);
    }

    /// Accept a browser's SDP offer for a pid, (lazily) start ddagrab+encode, and return the answer SDP.
    /// Null = no renderable window for that pid (minimized / wrong pid) or a negotiation failure.
    public async Task<string?> CreateAnswerAsync(int pid, string offerSdp)
    {
        PidSession session;
        lock (_lock)
        {
            if (_disposed) return null;
            // Replace a torn-down session (last-viewer teardown can race a re-offer); never hand back a dead one.
            if (!_sessions.TryGetValue(pid, out session!) || session.IsDead)
            {
                session?.Dispose();
                session = new PidSession(pid, _maxWidth, _fps, _bitrateKbps);
                _sessions[pid] = session;
            }
        }
        if (!await session.EnsureStartedAsync()) { RemoveIfCurrent(pid, session); return null; }
        return await session.AddPeerAsync(offerSdp, () => RemoveIfCurrent(pid, session));
    }

    public void Stop(int pid)
    {
        PidSession? s;
        lock (_lock) { _sessions.Remove(pid, out s); }
        s?.Dispose();
    }

    // Identity-checked removal: only evict the map entry if it's still THIS session (a newer one may
    // have replaced it). Prevents deleting a freshly-started session and orphaning its ffmpeg.
    private void RemoveIfCurrent(int pid, PidSession s)
    {
        lock (_lock) { if (_sessions.TryGetValue(pid, out var cur) && ReferenceEquals(cur, s)) _sessions.Remove(pid); }
        s.Dispose();
    }

    public void Dispose()
    {
        List<PidSession> all;
        lock (_lock) { _disposed = true; all = _sessions.Values.ToList(); _sessions.Clear(); }
        foreach (var s in all) s.Dispose();
    }

    // ── One pid's ddagrab encode + viewers ───────────────────────────────────────────────────────
    private sealed class PidSession : IDisposable
    {
        private readonly int _pid, _maxWidth, _fps, _bitrateKbps;
        private readonly List<RTCPeerConnection> _peers = new();
        private readonly object _peerLock = new();
        private readonly SemaphoreSlim _startGate = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Process? _ff;
        private VideoCapture? _cap;
        private Action? _onEmpty;
        private volatile bool _started, _dead;
        private long _lastSendMs;

        public bool IsDead => _dead;

        public PidSession(int pid, int maxWidth, int fps, int bitrateKbps)
        { _pid = pid; _maxWidth = maxWidth; _fps = fps; _bitrateKbps = bitrateKbps; }

        /// Start ddagrab+ffmpeg once. False if no window. Process.Start runs under a per-session gate
        /// (not the service lock), so a slow spawn never blocks other pids.
        public async Task<bool> EnsureStartedAsync()
        {
            if (_started) return !_dead;
            await _startGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_started) return !_dead;
                if (_dead) return false;
                if (!StartFfmpegLocked()) return false;
                _started = true;
                return true;
            }
            finally { _startGate.Release(); }
        }

        // Start PrintWindow capture -> ffmpeg rawvideo (libx264 Baseline, the config proven to decode on the
        // user's iPhone Safari). Occlusion-proof + move-proof (capture is by-HWND, not screen region) so no
        // watcher needed. ddagrab/60fps was a separate experiment that Safari rejected — reverted here.
        private bool StartFfmpegLocked()
        {
            var cap = VideoCapture.Open(_pid, _maxWidth);
            if (cap is null) return false;
            int gop = Math.Max(8, _fps / 2);
            string ffArgs =
                $"-hide_banner -loglevel error -f rawvideo -pixel_format bgr24 " +
                $"-video_size {cap.Width}x{cap.Height} -framerate {_fps} -use_wallclock_as_timestamps 1 -i - " +
                $"-an -pix_fmt yuv420p -c:v libx264 -profile:v baseline -preset ultrafast -tune zerolatency " +
                $"-b:v {_bitrateKbps}k -x264-params repeat-headers=1:bframes=0 -g {gop} -bf 0 " +
                $"-bsf:v h264_metadata=aud=insert -f h264 -";

            Process? ff;
            try { ff = Process.Start(new ProcessStartInfo("ffmpeg", ffArgs)
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception ex) { AgentLog.Warn($"[video] ffmpeg start failed pid {_pid}: {ex.Message}"); return false; }
            if (ff is null) return false;

            ff.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AgentLog.Debug($"[video ff {_pid}] {e.Data}"); };
            ff.BeginErrorReadLine();
            _cap = cap; _ff = ff;
            AgentLog.Info($"[video] pid {_pid} PrintWindow {cap.Width}x{cap.Height} @{_fps}fps libx264 (Safari-proven)");
            var stdin = ff.StandardInput.BaseStream;
            var stdout = ff.StandardOutput.BaseStream;
            _ = Task.Run(() => CapturePump(cap, stdin, ff));
            _ = Task.Run(() => ReadAccessUnits(stdout, ff));
            return true;
        }

        // Grab PrintWindow frames at ~_fps and feed tightly-packed BGR24 into ffmpeg's stdin. Bound to the
        // ffmpeg instance that owns this pump so a teardown's pump exits cleanly. Closes stdin on exit (EOF).
        private void CapturePump(VideoCapture cap, Stream stdin, Process owner)
        {
            int frameMs = Math.Max(1, 1000 / _fps);
            var sw = Stopwatch.StartNew();
            long next = 0;
            int fails = 0;
            try
            {
                while (!_dead && ReferenceEquals(_ff, owner))
                {
                    if (cap.TryGrab(out byte[] bgr))
                    {
                        fails = 0;
                        try { stdin.Write(bgr, 0, bgr.Length); stdin.Flush(); } catch { break; }   // ffmpeg gone
                    }
                    else if (++fails > 90) { AgentLog.Info($"[video] pid {_pid} capture failing — retiring"); Retire(); break; }
                    next += frameMs;
                    long wait = next - sw.ElapsedMilliseconds;
                    if (wait > 1) Thread.Sleep((int)wait);
                    else if (wait < -200) next = sw.ElapsedMilliseconds;
                }
            }
            catch { }
            try { stdin.Close(); } catch { }
        }

        public async Task<string?> AddPeerAsync(string offerSdp, Action onEmpty)
        {
            _onEmpty = onEmpty;
            var pc = new RTCPeerConnection(new RTCConfiguration());   // host candidates only (Tailscale, no TURN)
            // Level 3.1 (42e01f) = the config Safari is PROVEN to accept (it only decodes up to the level it
            // offered; answering above → BLACK). 720p30 is exactly Level 3.1's ceiling (3600 MBs * 30).
            var h264 = new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.video, 96, "H264", 90000, 0,
                                                    "packetization-mode=1;profile-level-id=42e01f");
            var track = new MediaStreamTrack(SDPMediaTypesEnum.video, false,
                                             new List<SDPAudioVideoMediaFormat> { h264 }, MediaStreamStatusEnum.SendOnly);
            pc.addTrack(track);
            pc.onconnectionstatechange += (state) =>
            {
                AgentLog.Info($"[video] pid {_pid} pc state: {state}");
                if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
                    RemovePeer(pc);
            };
            pc.oniceconnectionstatechange += (s) => AgentLog.Info($"[video] pid {_pid} ice: {s}");
            pc.OnRtcpBye += (_) => RemovePeer(pc);

            try
            {
                pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
                var answer = pc.createAnswer(null);
                await pc.setLocalDescription(answer);
                for (int i = 0; i < 20 && pc.iceGatheringState != RTCIceGatheringState.complete; i++)
                    await Task.Delay(50);
            }
            catch (Exception ex) { AgentLog.Warn($"[video] negotiate failed pid {_pid}: {ex.Message}"); pc.Close("negotiate failed"); return null; }

            lock (_peerLock) _peers.Add(pc);
            return pc.localDescription.sdp.ToString();
        }

        private void RemovePeer(RTCPeerConnection pc)
        {
            bool empty;
            lock (_peerLock) { _peers.Remove(pc); empty = _peers.Count == 0; }
            try { pc.Close("peer gone"); } catch { }
            if (empty)
            {
                AgentLog.Info($"[video] last viewer left pid {_pid} — stopping ddagrab/encode");
                Retire();
            }
        }

        // Retire = mark dead, stop the watcher + ffmpeg, and ask the service to drop us (identity-checked).
        private void Retire()
        {
            MarkDead();
            _onEmpty?.Invoke();
        }

        private void MarkDead()
        {
            if (_dead) return;
            _dead = true;
            try { _cts.Cancel(); } catch { }
            StopFfmpeg();
        }

        // Kill ffmpeg and CONFIRM it died (don't drop the handle on a swallowed Kill — that's how orphans happen).
        private void StopFfmpeg()
        {
            _cap = null;
            Process? ff = Interlocked.Exchange(ref _ff, null);
            if (ff is null) return;
            try { ff.Kill(true); } catch (Exception ex) { AgentLog.Debug($"[video] kill pid {_pid}: {ex.Message}"); }
            try { ff.WaitForExit(1500); } catch { }
            try { ff.Dispose(); } catch { }
        }

        // ffmpeg raw H.264 (AUD-delimited) -> one access unit per SendVideo, wall-clock paced timestamps.
        // Bound to the ffmpeg instance that produced this stream, so a restart's old pump exits cleanly.
        private void ReadAccessUnits(Stream stdout, Process owner)
        {
            var acc = new List<byte>(1 << 18);
            var au = new MemoryStream();
            var chunk = new byte[65536];
            int read;
            try
            {
                while (!_dead && ReferenceEquals(_ff, owner) && (read = stdout.Read(chunk, 0, chunk.Length)) > 0)
                {
                    for (int i = 0; i < read; i++) acc.Add(chunk[i]);
                    var starts = FindStartCodes(acc);
                    if (starts.Count < 2) continue;
                    int last = starts.Count - 1;
                    for (int k = 0; k < last; k++) EmitNal(acc, starts[k], starts[k + 1], au);
                    acc.RemoveRange(0, starts[last]);
                }
            }
            catch { }
            if (ReferenceEquals(_ff, owner)) FlushAu(au);
        }

        private static List<int> FindStartCodes(List<byte> a)
        {
            var idx = new List<int>();
            for (int i = 0; i + 2 < a.Count; i++)
                if (a[i] == 0 && a[i + 1] == 0 && a[i + 2] == 1) { idx.Add(i > 0 && a[i - 1] == 0 ? i - 1 : i); i += 2; }
            return idx;
        }

        private void EmitNal(List<byte> acc, int from, int to, MemoryStream au)
        {
            int p = from;
            while (p + 2 < to && !(acc[p] == 0 && acc[p + 1] == 0 && acc[p + 2] == 1)) p++;
            int hdr = p + 3;
            if (hdr >= to) return;
            int type = acc[hdr] & 0x1F;
            if (type == 9 && au.Length > 0) FlushAu(au);
            for (int i = from; i < to; i++) au.WriteByte(acc[i]);
        }

        private void FlushAu(MemoryStream au)
        {
            if (au.Length == 0) return;
            byte[] sample = au.ToArray();
            au.SetLength(0);

            long now = _clock.ElapsedMilliseconds;
            uint dur = _lastSendMs == 0 ? (uint)(90000 / _fps)
                                        : (uint)Math.Clamp((now - _lastSendMs) * 90, 900, 90000);
            _lastSendMs = now;

            List<RTCPeerConnection> snapshot;
            lock (_peerLock) snapshot = new List<RTCPeerConnection>(_peers);
            foreach (var pc in snapshot)
            {
                if (pc.connectionState != RTCPeerConnectionState.connected) continue;
                try { pc.SendVideo(dur, sample); } catch { }
            }
        }

        public void Dispose()
        {
            List<RTCPeerConnection> peers;
            lock (_peerLock) { peers = _peers.ToList(); _peers.Clear(); }
            foreach (var pc in peers) { try { pc.Close("session disposed"); } catch { } }
            MarkDead();
            try { _startGate.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }
}
