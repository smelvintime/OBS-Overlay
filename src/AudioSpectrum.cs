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

    const int LOOPBACK = 0x00020000;
    const int SHARED = 0;
    const int SILENT = 0x2;          // AUDCLNT_BUFFERFLAGS_SILENT

    // --------------------------------------------------------------- settings
    public const int Bands = 24;     // log-spaced bands sent to the browser
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

    public static string Status { get { return _status; } }
    public static bool Active { get { return _running && _silentFor < 60; } }

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
      while (true) {
        try { CaptureOnce(); }
        catch (Exception ex) { _status = "error: " + ex.Message; }
        _running = false;
        Thread.Sleep(2000);
      }
    }

    static void CaptureOnce() {
      var en = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
      IMMDevice dev;
      if (en.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out dev) != 0 || dev == null) {
        _status = "no output device"; return;
      }

      Guid iidClient = typeof(IAudioClient).GUID;
      object o;
      if (dev.Activate(ref iidClient, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out o) != 0) {
        _status = "could not open audio client"; return;
      }
      var client = (IAudioClient)o;

      IntPtr pFmt;
      if (client.GetMixFormat(out pFmt) != 0) { _status = "no mix format"; return; }
      var fmt = (WAVEFORMATEX)Marshal.PtrToStructure(pFmt, typeof(WAVEFORMATEX));

      int hr = client.Initialize(SHARED, LOOPBACK, 10000000L /*1s buffer*/, 0, pFmt, IntPtr.Zero);
      if (hr != 0) { _status = "initialise failed 0x" + hr.ToString("x8"); return; }

      Guid iidCap = typeof(IAudioCaptureClient).GUID;
      object oc;
      if (client.GetService(ref iidCap, out oc) != 0) { _status = "no capture service"; return; }
      var cap = (IAudioCaptureClient)oc;

      lock (_lock) { _rate = fmt.nSamplesPerSec; }
      client.Start();
      _running = true;
      _status = "capturing " + fmt.nChannels + "ch " + fmt.nSamplesPerSec + "Hz";

      int channels = Math.Max(1, (int)fmt.nChannels);
      int bytesPerSample = fmt.wBitsPerSample / 8;
      int stride = channels * bytesPerSample;

      try {
        while (true) {
          uint next;
          if (cap.GetNextPacketSize(out next) != 0) break;
          if (next == 0) { Thread.Sleep(4); continue; }

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
        try { client.Stop(); } catch { }
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

      for (int b = 0; b < Bands; b++) {
        double f0 = LowHz * Math.Pow(HighHz / LowHz, (double)b / Bands);
        double f1 = LowHz * Math.Pow(HighHz / LowHz, (double)(b + 1) / Bands);
        int i0 = (int)(f0 / nyquist * half);
        int i1 = (int)(f1 / nyquist * half);
        if (i1 <= i0) i1 = i0 + 1;
        if (i1 > half) i1 = half;

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

      for (int b = 0; b < Bands; b++) {
        double norm = raw[b] / _agc;                        // 0..1
        if (norm > 1) norm = 1;
        // Perceptual curve: loudness is roughly logarithmic, so without this
        // everything below the peak looks nearly flat. The 1.25 headroom lets
        // the strongest bands actually reach the top of the meter.
        double shaped = Math.Pow(norm, 0.45) * 1.25;
        if (shaped > 1) shaped = 1;
        // Fast attack, slow release - the classic VU meter feel.
        if (shaped > _smoothed[b]) _smoothed[b] += (shaped - _smoothed[b]) * 0.6;
        else _smoothed[b] += (shaped - _smoothed[b]) * 0.14;
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
