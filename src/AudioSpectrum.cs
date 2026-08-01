// Live audio spectrum via WASAPI loopback.
//
// Captures whatever Windows is playing on the default output device, runs an
// FFT over it, and exposes log-spaced frequency bands so the overlay's
// equaliser can move with the music instead of animating on a timer.
//
// Capturing the output device rather than any single app means it works no
// matter what is playing - Spotify, iTunes, a browser - with no per-app setup.

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace NowPlaying {

  static class AudioSpectrum {

    // ------------------------------------------------------------ COM interop
    // Every method below returns a raw HRESULT, so all need [PreserveSig];
    // without it .NET rewrites the signature and the out-params come back wrong.
    // Vtable order is load-bearing: stubs stand in for methods we never call,
    // and one missing stub silently calls the wrong function.

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator {
      [PreserveSig] int EnumAudioEndpoints_Stub();
      [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice dev);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice {
      [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p,
                                 [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient {
      [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufDuration,
                                   long periodicity, IntPtr fmt, IntPtr session);
      [PreserveSig] int GetBufferSize(out uint frames);
      [PreserveSig] int GetStreamLatency(out long latency);
      [PreserveSig] int GetCurrentPadding(out uint padding);
      [PreserveSig] int IsFormatSupported(int shareMode, IntPtr fmt, out IntPtr closest);
      [PreserveSig] int GetMixFormat(out IntPtr fmt);
      [PreserveSig] int GetDevicePeriod(out long def, out long min);
      [PreserveSig] int Start();
      [PreserveSig] int Stop();
      [PreserveSig] int Reset();
      [PreserveSig] int SetEventHandle(IntPtr h);
      [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object svc);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient {
      [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags,
                                  out ulong devPos, out ulong qpcPos);
      [PreserveSig] int ReleaseBuffer(uint frames);
      [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct WAVEFORMATEX {
      public ushort wFormatTag, nChannels;
      public uint nSamplesPerSec, nAvgBytesPerSec;
      public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    // ---------------------------------------------- per-process loopback
    // Capturing the whole output device means Discord voice or a YouTube tab
    // moves the bars while the overlay shows a Spotify track. Windows can
    // capture a single process tree instead, which keeps the equaliser tied to
    // the player the overlay is actually displaying.

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation {
      [PreserveSig] int GetActivateResult(out int activateResult,
                                          [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler {
      [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    // Marker interface. Windows completes the activation on another apartment,
    // so the callback must declare itself agile - without this the activation
    // call fails outright with E_ILLEGAL_METHOD_CALL.
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject {
      public readonly ManualResetEvent Done = new ManualResetEvent(false);
      public int Hr;
      public object Activated;
      public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) {
        try { op.GetActivateResult(out Hr, out Activated); }
        catch { Hr = -1; }
        Done.Set();
        return 0;
      }
    }

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync(
      [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
      ref Guid riid, IntPtr activationParams,
      IActivateAudioInterfaceCompletionHandler completionHandler,
      out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    struct ACTIVATION_PARAMS {
      public int ActivationType;        // 1 = PROCESS_LOOPBACK
      public int TargetProcessId;
      public int ProcessLoopbackMode;   // 0 = INCLUDE_TARGET_PROCESS_TREE
    }

    // PROPVARIANT carrying a BLOB. On x64: vt + reserved is 8 bytes, cbSize at
    // 8, four bytes of padding, then the pointer at 16.
    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT_BLOB {
      public ushort vt; public ushort r1, r2, r3;
      public uint cbSize; public uint pad;
      public IntPtr pBlobData;
    }

    const string VAD_PROCESS_LOOPBACK = "VAD\\Process_Loopback";
    const int VT_BLOB = 65;
    const int EVENTCALLBACK = 0x00040000;

    [DllImport("kernel32.dll")] static extern IntPtr CreateEventW(IntPtr a, bool manual, bool init, IntPtr name);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr p, int coInit);

    const int LOOPBACK = 0x00020000;
    const int SHARED = 0;
    const int SILENT = 0x2;          // AUDCLNT_BUFFERFLAGS_SILENT

    // --------------------------------------------------------------- settings
    // 32 matches the overlay's maximum ?bars=, so every bar count up to the
    // slider's top maps one-band-per-bar and no two bars can share a source.
    // (At 24 bands, 26 bars meant the left three bars mirrored each other:
    // two bars shared band 0 and band 1 WAS band 0 - see Compute's bin
    // allocation for the other half of that bug.)
    public const int Bands = 32;     // log-spaced bands sent to the browser
    const int FftSize = 2048;        // ~43ms at 48kHz: responsive but stable
    const double LowHz = 35;
    const double HighHz = 16000;

    // ------------------------------------------------------------------ state
    static readonly object _lock = new object();
    static readonly float[] _ring = new float[FftSize];
    static int _ringPos;
    static uint _rate = 48000;
    static volatile bool _running;
    static volatile string _status = "not started";
    static volatile int _silentFor;                    // consecutive silent reads

    static readonly double[] _smoothed = new double[Bands];
    static double _agc = 0.02;                         // adapts to overall volume
    // Each band's own recent peak, released far more slowly than _agc. This is
    // what stops bass-heavy music from pinning the left-hand bars - see the long
    // note in Compute() for why the global _agc alone could never do it.
    static readonly double[] _ref = new double[Bands];

    public static string Status { get { return _status; } }

    // "Is capture working", NOT "is sound present". Tying this to silence made
    // pausing the music look like a capture failure, so the overlay fell back to
    // the canned animation and the bars danced with nothing playing. Silence
    // should simply produce zero-height bars.
    public static bool Active { get { return _running; } }

    // Which process the equaliser should listen to. 0 means "whole output
    // device", which is the fallback when the player cannot be identified.
    static volatile int _targetPid;
    static volatile string _targetName = "";
    public static string Target { get { return _targetPid == 0 ? "(all system audio)" : _targetName; } }

    /// <summary>
    /// Point the analyser at the app the overlay is currently showing, so other
    /// audio (voice chat, a video in a browser tab) cannot move the bars.
    /// </summary>
    public static void SetTargetApp(string appId) {
      int pid = ResolvePid(appId);
      if (pid != _targetPid) {
        _targetPid = pid;
        _targetName = appId ?? "";
        _restart = true;          // capture loop tears down and re-activates
      }
    }

    static volatile bool _restart;

    // "Spotify.exe" -> Spotify, "AppleInc.iTunes_xyz!iTunes" -> iTunes.
    static int ResolvePid(string appId) {
      if (string.IsNullOrEmpty(appId)) return 0;
      string hint = appId;
      int bang = hint.LastIndexOf('!');
      if (bang >= 0 && bang < hint.Length - 1) hint = hint.Substring(bang + 1);
      if (hint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        hint = hint.Substring(0, hint.Length - 4);
      System.Diagnostics.Process[] procs = null;
      try {
        procs = System.Diagnostics.Process.GetProcessesByName(hint);
        if (procs.Length == 0) return 0;
        // Capture includes the target's whole process tree, so aim at the parent:
        // browsers and Spotify play audio from child processes.
        foreach (var p in procs) {
          try { if (p.MainWindowHandle != IntPtr.Zero) return p.Id; } catch { }
        }
        var best = procs[0];
        foreach (var p in procs) {
          try { if (p.StartTime < best.StartTime) best = p; } catch { }
        }
        return best.Id;
      } catch { return 0; }
      finally {
        // Called once a second from the poller; each Process object holds an
        // OS handle, and waiting on the GC to close them is a slow handle leak.
        if (procs != null) foreach (var p in procs) { try { p.Dispose(); } catch { } }
      }
    }

    // Analysis runs on a fixed clock rather than per request. Doing it per
    // request would advance the smoothing and gain state at a rate that depends
    // on how many browsers are connected, so two viewers would see different
    // frames and the bars would speed up as clients joined.
    static volatile int[] _bandsOut = new int[Bands];

    public static void Start() {
      var cap = new Thread(CaptureLoop);
      cap.IsBackground = true;
      cap.Priority = ThreadPriority.AboveNormal;   // audio capture must keep up
      cap.Start();

      var an = new Thread(AnalyzeLoop);
      an.IsBackground = true;
      an.Start();
    }

    static void AnalyzeLoop() {
      while (true) {
        try { _bandsOut = Compute(); } catch { }
        Thread.Sleep(33);      // ~30fps, matching the SSE frame rate
      }
    }

    /// <summary>Latest band levels, 0-100, low frequency first.</summary>
    public static int[] Read() { return _bandsOut; }

    // Reconnects on its own: switching output device, or unplugging headphones,
    // invalidates the client, and the overlay should recover without a restart.
    static void CaptureLoop() {
      // Once per thread, not once per attempt - repeated init calls only
      // stack up apartment ref counts that nothing ever undoes.
      CoInitializeEx(IntPtr.Zero, 0 /*COINIT_MULTITHREADED*/);
      int failSecs = 2;
      while (true) {
        try { CaptureOnce(); }
        catch (Exception ex) { _status = "error: " + ex.Message; }
        bool wasCapturing = _running;
        _running = false;
        // A session that actually captured, or a deliberate target switch,
        // deserves a quick retry. A session that never got going will almost
        // certainly fail the same way next time, and re-activating WASAPI
        // every two seconds forever churns the audio engine (and its driver)
        // for nothing - back off instead, and snap back the moment a session
        // works again so recovery after a device change stays fast.
        if (wasCapturing || _restart) failSecs = 2;
        else failSecs = Math.Min(failSecs * 2, 30);
        Thread.Sleep(failSecs * 1000);
      }
    }

    // Activates a client that hears only the given process tree. Returns null if
    // the OS is too old for process loopback, so the caller can fall back.
    static IAudioClient ActivateProcessLoopback(int pid) {
      IntPtr pAp = IntPtr.Zero, pPv = IntPtr.Zero;
      try {
        var ap = new ACTIVATION_PARAMS {
          ActivationType = 1, TargetProcessId = pid, ProcessLoopbackMode = 0
        };
        pAp = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ACTIVATION_PARAMS)));
        Marshal.StructureToPtr(ap, pAp, false);

        var pv = new PROPVARIANT_BLOB {
          vt = VT_BLOB,
          cbSize = (uint)Marshal.SizeOf(typeof(ACTIVATION_PARAMS)),
          pBlobData = pAp
        };
        pPv = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PROPVARIANT_BLOB)));
        Marshal.StructureToPtr(pv, pPv, false);

        var handler = new ActivationHandler();
        Guid iid = typeof(IAudioClient).GUID;
        IActivateAudioInterfaceAsyncOperation op;
        ActivateAudioInterfaceAsync(VAD_PROCESS_LOOPBACK, ref iid, pPv, handler, out op);
        if (!handler.Done.WaitOne(3000)) return null;
        if (handler.Hr != 0 || handler.Activated == null) return null;
        return (IAudioClient)handler.Activated;
      } catch {
        return null;
      } finally {
        if (pPv != IntPtr.Zero) Marshal.FreeHGlobal(pPv);
        if (pAp != IntPtr.Zero) Marshal.FreeHGlobal(pAp);
      }
    }

    static void CaptureOnce() {
      _restart = false;
      int pid = _targetPid;

      IAudioClient client = null;
      // Every COM object taken here is released in the one finally below.
      // These are RCWs over live WASAPI objects: audio clients hold buffers
      // and session state inside audiodg.exe, and leaving them to whatever
      // GC eventually finalises them lets dead clients accumulate across
      // reconnects on a process that can run for days.
      object enObj = null, devObj = null, capObj = null, failedProcClient = null;
      WAVEFORMATEX fmt;
      IntPtr pFmt = IntPtr.Zero;
      bool pFmtIsHGlobal = false;   // GetMixFormat allocates CoTaskMem instead
      IntPtr hEvent = IntPtr.Zero;
      bool perProcess = false;

      try {
        if (pid != 0) client = ActivateProcessLoopback(pid);

        if (client != null) {
          perProcess = true;
          // Process loopback makes us state the format; 16-bit PCM is always safe.
          fmt = new WAVEFORMATEX {
            wFormatTag = 1, nChannels = 2, nSamplesPerSec = 48000,
            wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = 48000 * 4, cbSize = 0
          };
          pFmt = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEFORMATEX)));
          pFmtIsHGlobal = true;
          Marshal.StructureToPtr(fmt, pFmt, false);
          hEvent = CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
          int phr = client.Initialize(SHARED, LOOPBACK | EVENTCALLBACK, 10000000L, 0, pFmt, IntPtr.Zero);
          if (phr != 0) {
            _status = "process capture init failed 0x" + phr.ToString("x8");
            failedProcClient = client;     // fall through to whole-device capture
            client = null;
            perProcess = false;
            Marshal.FreeHGlobal(pFmt); pFmt = IntPtr.Zero; pFmtIsHGlobal = false;
          } else {
            client.SetEventHandle(hEvent);
          }
        }

        if (client == null) {
          var en = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
          enObj = en;
          IMMDevice dev;
          if (en.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out dev) != 0 || dev == null) {
            _status = "no output device"; return;
          }
          devObj = dev;
          Guid iidClient = typeof(IAudioClient).GUID;
          object o;
          if (dev.Activate(ref iidClient, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out o) != 0) {
            _status = "could not open audio client"; return;
          }
          client = (IAudioClient)o;
          if (client.GetMixFormat(out pFmt) != 0) { _status = "no mix format"; return; }
          fmt = (WAVEFORMATEX)Marshal.PtrToStructure(pFmt, typeof(WAVEFORMATEX));
          int hr2 = client.Initialize(SHARED, LOOPBACK, 10000000L /*1s buffer*/, 0, pFmt, IntPtr.Zero);
          if (hr2 != 0) { _status = "initialise failed 0x" + hr2.ToString("x8"); return; }
        } else {
          fmt = (WAVEFORMATEX)Marshal.PtrToStructure(pFmt, typeof(WAVEFORMATEX));
        }

        Guid iidCap = typeof(IAudioCaptureClient).GUID;
        object oc;
        if (client.GetService(ref iidCap, out oc) != 0) { _status = "no capture service"; return; }
        capObj = oc;
        var cap = (IAudioCaptureClient)oc;

        lock (_lock) { _rate = fmt.nSamplesPerSec; }
        client.Start();
        _running = true;
        _status = (perProcess ? "capturing " + _targetName + " only" : "capturing all system audio")
                  + " (" + fmt.nChannels + "ch " + fmt.nSamplesPerSec + "Hz)";

        int channels = Math.Max(1, (int)fmt.nChannels);
        int bytesPerSample = fmt.wBitsPerSample / 8;
        int stride = channels * bytesPerSample;

        while (true) {
          if (_restart) break;          // the overlay switched to another player
          if (perProcess) WaitForSingleObject(hEvent, 200);

          uint next;
          if (cap.GetNextPacketSize(out next) != 0) break;
          if (next == 0) {
            if (!perProcess) Thread.Sleep(4);
            else _silentFor++;          // event-driven: no packet means no sound
            continue;
          }

          IntPtr data; uint frames, flags; ulong dp, qp;
          if (cap.GetBuffer(out data, out frames, out flags, out dp, out qp) != 0) break;

          bool silent = (flags & SILENT) != 0 || data == IntPtr.Zero;
          if (silent) { _silentFor++; }
          else {
            _silentFor = 0;
            lock (_lock) {
              for (int i = 0; i < frames; i++) {
                float v;
                int off = i * stride;
                if (fmt.wBitsPerSample == 32) {
                  v = ReadFloat(data, off);
                  if (channels > 1) v = (v + ReadFloat(data, off + bytesPerSample)) * 0.5f;
                } else if (fmt.wBitsPerSample == 16) {
                  v = Marshal.ReadInt16(data, off) / 32768f;
                  if (channels > 1) v = (v + Marshal.ReadInt16(data, off + 2) / 32768f) * 0.5f;
                } else continue;
                _ring[_ringPos] = v;
                _ringPos = (_ringPos + 1) % FftSize;
              }
            }
          }
          cap.ReleaseBuffer(frames);
        }
      } finally {
        if (client != null) { try { client.Stop(); } catch { } }
        if (hEvent != IntPtr.Zero) { try { CloseHandle(hEvent); } catch { } }
        if (pFmt != IntPtr.Zero) {
          // Two allocators, two frees: our own AllocHGlobal for the stated
          // process-loopback format, CoTaskMem from GetMixFormat otherwise.
          if (pFmtIsHGlobal) { try { Marshal.FreeHGlobal(pFmt); } catch { } }
          else { try { Marshal.FreeCoTaskMem(pFmt); } catch { } }
        }
        if (capObj != null) { try { Marshal.ReleaseComObject(capObj); } catch { } }
        if (client != null) { try { Marshal.ReleaseComObject(client); } catch { } }
        if (failedProcClient != null) { try { Marshal.ReleaseComObject(failedProcClient); } catch { } }
        if (devObj != null) { try { Marshal.ReleaseComObject(devObj); } catch { } }
        if (enObj != null) { try { Marshal.ReleaseComObject(enObj); } catch { } }
      }
    }

    static float ReadFloat(IntPtr p, int off) {
      return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(p, off)), 0);
    }

    // ------------------------------------------------------------------- FFT
    static readonly double[] _re = new double[FftSize];
    static readonly double[] _im = new double[FftSize];

    static int[] Compute() {
      var outp = new int[Bands];
      uint rate;
      lock (_lock) {
        rate = _rate;
        int pos = _ringPos;
        for (int i = 0; i < FftSize; i++) {
          // Hann window keeps neighbouring bands from bleeding into each other
          double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
          _re[i] = _ring[(pos + i) % FftSize] * w;
          _im[i] = 0;
        }
      }

      Transform(_re, _im);

      double nyquist = rate / 2.0;
      int half = FftSize / 2;
      var raw = new double[Bands];
      double frameMax = 0;

      // Each band owns bins no earlier band used. The log spacing makes the
      // bottom bands narrower than one FFT bin (23Hz at 48k/2048), and the
      // naive floor() mapping handed the SAME bin to two neighbouring bands -
      // which is why the far-left bars moved in perfect lockstep. Forcing the
      // start past the previous band's end costs a little frequency accuracy
      // at the very bottom and buys every bar its own signal. Bin 0 is DC and
      // never belongs to any band.
      int prevEnd = 1;
      for (int b = 0; b < Bands; b++) {
        double f0 = LowHz * Math.Pow(HighHz / LowHz, (double)b / Bands);
        double f1 = LowHz * Math.Pow(HighHz / LowHz, (double)(b + 1) / Bands);
        int i0 = (int)(f0 / nyquist * half);
        int i1 = (int)(f1 / nyquist * half);
        if (i0 < prevEnd) i0 = prevEnd;
        if (i1 <= i0) i1 = i0 + 1;
        if (i1 > half) i1 = half;
        if (i0 >= half) { raw[b] = 0; continue; }   // ran off the top on a low rate
        prevEnd = i1;

        double peak = 0;
        for (int i = i0; i < i1; i++) {
          double m = Math.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);
          if (m > peak) peak = m;
        }
        // Music has far less energy up high; tilt so treble bars stay visible.
        peak *= 1.0 + 2.5 * ((double)b / Bands);
        raw[b] = peak;
        if (peak > frameMax) frameMax = peak;
      }

      // Automatic gain: track a decaying maximum so the bars look right whether
      // the system volume is at 10% or 100%, without ever dividing by zero.
      // Release is quick enough that one loud transient does not flatten the
      // meter for the next few seconds.
      if (frameMax > _agc) _agc = frameMax;                 // snap up
      else _agc += (frameMax - _agc) * 0.08;                // ease down
      if (_agc < 1e-5) _agc = 1e-5;

      // Why the global _agc cannot carry this on its own.
      //
      // _agc snaps up to frameMax the instant it rises, and frameMax in nearly
      // all music IS the bass. So the loudest band is dividing by itself every
      // frame and lands on 1.0 by construction. On bass-heavy tracks that meant
      // the left-hand bars sat welded to the top and the kick drum - the one
      // thing worth seeing - was invisible, because there was no headroom left
      // above the level the bass already held.
      //
      // Each band now also carries its own reference, released about five times
      // slower than _agc (0.015 against 0.08, so roughly a two-second memory
      // rather than half a second). A band is measured against what IT has been
      // doing lately instead of against whatever the loudest band is doing right
      // now. Sustained bass pushes its own reference up and then reads part-way
      // down it between hits, so the kick has somewhere to travel to.
      //
      // Two floors keep that from misbehaving. The relative floor stops a band
      // that is genuinely quiet from being self-normalised up into a full bar -
      // without it, tape hiss in a dead treble band would dance like a lead
      // instrument. The absolute floor is the same silence guard _agc already
      // had: in a silent room every reference decays toward the noise floor, and
      // dividing noise by noise is 1.0, which would light the whole meter up.
      const double PerBand = 0.75;      // 0 = old global behaviour, 1 = fully per-band
      for (int b = 0; b < Bands; b++) {
        if (raw[b] > _ref[b]) _ref[b] = raw[b];             // snap up on a transient
        else _ref[b] += (raw[b] - _ref[b]) * 0.015;         // then let go slowly

        double denom = (1 - PerBand) * _agc + PerBand * _ref[b];
        double relFloor = _agc * 0.12;
        if (denom < relFloor) denom = relFloor;
        if (denom < 1e-5) denom = 1e-5;

        double norm = raw[b] / denom;                       // 0..1
        if (norm > 1) norm = 1;
        // Perceptual curve: loudness is roughly logarithmic, so without this
        // everything below the peak looks nearly flat.
        //
        // The old curve multiplied by 1.25 to let the strongest bands reach the
        // top. Solving Math.Pow(n, 0.45) * 1.25 >= 1 gives n >= 0.61, so every
        // band above 61% of the reference clipped to a full bar and the top
        // third of the range did not exist. That alone flattened the loud end
        // even before the _agc problem above. Per-band references reach 1.0 on
        // their own at a real peak, so the multiplier is gone and the exponent
        // is gentler.
        double shaped = Math.Pow(norm, 0.55);
        if (shaped > 1) shaped = 1;
        // Fast attack, slower release - the classic VU meter feel. Release is
        // what governs how lively this looks: a slow one leaves the bars
        // hanging near their peak, so they drift rather than dance. These are
        // tuned well past a literal meter, because the point here is to read
        // the beat at a glance on a stream, not to measure anything.
        if (shaped > _smoothed[b]) _smoothed[b] += (shaped - _smoothed[b]) * 0.8;
        else _smoothed[b] += (shaped - _smoothed[b]) * 0.3;
        int v = (int)Math.Round(_smoothed[b] * 100);
        outp[b] = v < 0 ? 0 : (v > 100 ? 100 : v);
      }
      return outp;
    }

    // In-place iterative radix-2 FFT.
    static void Transform(double[] re, double[] im) {
      int n = re.Length;
      for (int i = 1, j = 0; i < n; i++) {
        int bit = n >> 1;
        for (; (j & bit) != 0; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) {
          double t = re[i]; re[i] = re[j]; re[j] = t;
          t = im[i]; im[i] = im[j]; im[j] = t;
        }
      }
      for (int len = 2; len <= n; len <<= 1) {
        double ang = -2 * Math.PI / len;
        double wr = Math.Cos(ang), wi = Math.Sin(ang);
        for (int i = 0; i < n; i += len) {
          double cr = 1, ci = 0;
          for (int k = 0; k < len / 2; k++) {
            int a = i + k, b = i + k + len / 2;
            double xr = re[b] * cr - im[b] * ci;
            double xi = re[b] * ci + im[b] * cr;
            re[b] = re[a] - xr; im[b] = im[a] - xi;
            re[a] += xr; im[a] += xi;
            double ncr = cr * wr - ci * wi;
            ci = cr * wi + ci * wr;
            cr = ncr;
          }
        }
      }
    }
  }
}
