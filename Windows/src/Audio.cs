using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GigaTTS;

static class AudioDevices
{
    public static List<AudioDevice> Inputs()
    {
        var result = new List<AudioDevice> { new("", "Системный") };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    result.Add(new AudioDevice(device.ID, device.FriendlyName));
                }
                catch
                {
                    // Skip a device that does not report a name.
                }
                finally
                {
                    try { device.Dispose(); } catch { /* Already released. */ }
                }
            }
        }
        catch
        {
            return result;
        }
        return result;
    }

    public static bool HasRenderDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                || enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        }
        catch
        {
            return false;
        }
    }
}

static class AudioConvert
{
    public static float[] ToMono(byte[] buffer, int count, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0)
            throw new InvalidOperationException("Неподдерживаемый формат аудио.");
        var frameSize = bytesPerSample * channels;
        var frames = count / frameSize;
        var output = new float[frames];
        var floating = IsFloat(format);
        for (var frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            var offset = frame * frameSize;
            for (var channel = 0; channel < channels; channel++)
            {
                var sample = offset + channel * bytesPerSample;
                if (floating && format.BitsPerSample == 32)
                    sum += BitConverter.ToSingle(buffer, sample);
                else if (format.BitsPerSample == 16)
                    sum += BitConverter.ToInt16(buffer, sample) / 32768f;
                else if (format.BitsPerSample == 32)
                    sum += BitConverter.ToInt32(buffer, sample) / 2147483648f;
                else
                    throw new InvalidOperationException($"Неподдерживаемый формат аудио: {format.BitsPerSample}-bit {format.Encoding}.");
            }
            output[frame] = sum / channels;
        }
        return output;
    }

    static bool IsFloat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            return true;
        if (format is WaveFormatExtensible extensible)
            return extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        return false;
    }
}

sealed class LinearResampler
{
    readonly List<float> _carry = new();
    double _step = 1;
    double _fraction;

    public void Reset(double sourceRate, double targetRate = 16000)
    {
        _step = Math.Max(sourceRate, 1) / targetRate;
        _carry.Clear();
        _fraction = 0;
    }

    public float[] Process(float[] input)
    {
        if (input.Length == 0)
            return Array.Empty<float>();
        _carry.AddRange(input);
        var output = new List<float>();
        var position = _fraction;
        while (position + 1 < _carry.Count)
        {
            var index = (int)position;
            var fraction = (float)(position - index);
            var left = _carry[index];
            var right = _carry[index + 1];
            output.Add(left + (right - left) * fraction);
            position += _step;
        }
        var consumed = (int)position;
        if (consumed > 0)
        {
            _carry.RemoveRange(0, Math.Min(consumed, _carry.Count));
            position -= consumed;
        }
        _fraction = position;
        return output.ToArray();
    }
}

sealed class WavWriter
{
    readonly object _gate = new();
    readonly FileStream _stream;
    readonly int _sampleRate;
    uint _bytes;
    bool _closed;

    public WavWriter(string path, int sampleRate = 16000)
    {
        Path = path;
        _sampleRate = sampleRate;
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Delete(path);
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _stream.Write(Header(0));
    }

    public string Path { get; }

    public double Duration
    {
        get
        {
            lock (_gate)
                return (_bytes / 2.0) / _sampleRate;
        }
    }

    public void Append(float[] samples)
    {
        if (samples.Length == 0)
            return;
        var pcm = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            var clamped = Math.Max(-1f, Math.Min(1f, samples[index]));
            var value = (short)Math.Round(clamped * 32767f);
            pcm[index * 2] = (byte)value;
            pcm[index * 2 + 1] = (byte)(value >> 8);
        }
        lock (_gate)
        {
            if (_closed)
                return;
            _stream.Write(pcm);
            _bytes += (uint)pcm.Length;
        }
    }

    public string Finish()
    {
        lock (_gate)
        {
            if (!_closed)
            {
                _stream.Position = 0;
                _stream.Write(Header(_bytes));
                _stream.Dispose();
                _closed = true;
            }
            return Path;
        }
    }

    byte[] Header(uint dataBytes)
    {
        var data = new byte[44];
        Write(data, 0, "RIFF");
        Write(data, 4, 36 + dataBytes);
        Write(data, 8, "WAVE");
        Write(data, 12, "fmt ");
        Write(data, 16, 16u);
        Write(data, 20, (ushort)1);
        Write(data, 22, (ushort)1);
        Write(data, 24, (uint)_sampleRate);
        Write(data, 28, (uint)_sampleRate * 2);
        Write(data, 32, (ushort)2);
        Write(data, 34, (ushort)16);
        Write(data, 36, "data");
        Write(data, 40, dataBytes);
        return data;
    }

    static void Write(byte[] data, int offset, string text)
    {
        for (var index = 0; index < text.Length; index++)
            data[offset + index] = (byte)text[index];
    }

    static void Write(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    static void Write(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }
}

sealed class CaptureSession : ICaptureSession
{
    WasapiCapture? _capture;
    MMDeviceEnumerator? _devices;
    WavWriter? _writer;
    LinearResampler? _resampler;
    bool _running;

    public event Action<string>? Failed;
    public bool IsRunning => _running;
    public double Duration => _writer?.Duration ?? 0;

    public void StartMicrophone(string path, string deviceId)
    {
        var devices = new MMDeviceEnumerator();
        var owned = false;
        try
        {
            var device = string.IsNullOrEmpty(deviceId)
                ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                : devices.GetDevice(deviceId);
            owned = true;
            Start(path, devices, new WasapiCapture(device));
        }
        catch
        {
            if (!owned)
                devices.Dispose();
            throw;
        }
    }

    public void StartLoopback(string path)
    {
        var devices = new MMDeviceEnumerator();
        var owned = false;
        try
        {
            var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            owned = true;
            Start(path, devices, new WasapiLoopbackCapture(device));
        }
        catch
        {
            if (!owned)
                devices.Dispose();
            throw;
        }
    }

    public string? Stop()
    {
        _running = false;
        var capture = _capture;
        _capture = null;
        if (capture != null)
        {
            try { capture.StopRecording(); } catch { /* The device may already be gone. */ }
            try { capture.Dispose(); } catch { /* Released with the device. */ }
        }
        try { _devices?.Dispose(); } catch { /* Already released. */ }
        _devices = null;
        var path = _writer?.Finish();
        _writer = null;
        _resampler = null;
        return path;
    }

    void Start(string path, MMDeviceEnumerator devices, WasapiCapture capture)
    {
        Stop();
        _devices = devices;
        _writer = new WavWriter(path);
        _resampler = new LinearResampler();
        _resampler.Reset(capture.WaveFormat.SampleRate);
        _capture = capture;
        _running = true;
        capture.DataAvailable += (_, args) =>
        {
            if (!_running || args.BytesRecorded <= 0 || _writer == null || _resampler == null)
                return;
            try
            {
                var mono = AudioConvert.ToMono(args.Buffer, args.BytesRecorded, capture.WaveFormat);
                _writer.Append(_resampler.Process(mono));
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex.Message);
            }
        };
        capture.RecordingStopped += (_, args) =>
        {
            if (_running && args.Exception != null)
                Failed?.Invoke(args.Exception.Message);
        };
        try
        {
            capture.StartRecording();
        }
        catch (Exception ex)
        {
            var started = path;
            Stop();
            TryDelete(started);
            throw new InvalidOperationException("Не удалось начать запись. " + ex.Message, ex);
        }
    }

    static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* The next recording replaces it. */ }
    }
}

sealed class WindowsPlayback : IPlayback
{
    readonly List<Voice> _voices = new();
    bool _stopping;

    public bool IsPlaying { get; private set; }
    public event Action? Ended;

    public void Start(IReadOnlyList<string> paths)
    {
        Stop();
        foreach (var path in paths)
        {
            try
            {
                var voice = new Voice(path);
                voice.Output.PlaybackStopped += (_, _) =>
                {
                    if (_stopping)
                        return;
                    if (_voices.All(item => item.Output.PlaybackState != PlaybackState.Playing))
                        Ended?.Invoke();
                };
                _voices.Add(voice);
                voice.Output.Play();
            }
            catch
            {
                // One unreadable track should not block the other.
            }
        }
        IsPlaying = _voices.Count > 0;
    }

    public void Stop()
    {
        if (_voices.Count == 0)
        {
            IsPlaying = false;
            return;
        }
        _stopping = true;
        foreach (var voice in _voices.ToArray())
        {
            try { voice.Output.Stop(); } catch { /* Already stopped. */ }
            voice.Dispose();
        }
        _voices.Clear();
        _stopping = false;
        IsPlaying = false;
    }

    sealed class Voice : IDisposable
    {
        public Voice(string path)
        {
            Reader = new AudioFileReader(path);
            Output = new WaveOutEvent();
            Output.Init(Reader);
        }

        public WaveOutEvent Output { get; }
        public AudioFileReader Reader { get; }

        public void Dispose()
        {
            try { Output.Dispose(); } catch { /* Already disposed. */ }
            try { Reader.Dispose(); } catch { /* Already disposed. */ }
        }
    }
}
