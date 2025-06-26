using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Reflection;
using System.Xml.Linq;

namespace AudioClone.CoreCapture
{
    public class AudioProvider : IDisposable
    {
        private const int MB = 1024 * 1024;
        private readonly WasapiLoopbackCapture loopbackWaveIn;
        private readonly MyAudioStream recordingStream;
        private readonly IWaveProvider pcmStream;
        private readonly Thread waveThread;
        private bool isRunning = true;

        public int bitRate { get; private set; } = 16;
        public int channels { get; private set; } = 2;
        public int sampleRate { get; private set; } = 48000;
        public bool isMp3Ready { get; private set; } = false;
        public ConcurrentDictionary<Guid, Tuple<string, string>> SubscribedClients = new();
        public ConcurrentQueue<byte>? rawBuffer = null;

        private readonly ConcurrentDictionary<Guid, MyAudioStream> pcmSubscribers = new();

        public WaveFormat PcmFormat => pcmStream.WaveFormat;
        public int PcmBlockAlign => pcmStream.WaveFormat.BlockAlign;

        public static ConcurrentBag<Guid> ListeningClients = new();

        public static long listenClientsCount => ListeningClients.Count;

        static object locker = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioProvider"/> class, which captures audio from a specified
        /// device or the default audio endpoint.
        /// </summary>
        /// <remarks>This constructor sets up audio capture using WASAPI loopback. If a specific device ID
        /// is provided, the corresponding device is used; otherwise, the default multimedia audio endpoint is selected.
        /// The captured audio is processed and optionally resampled to the specified <paramref
        /// name="targetFormat"/>.</remarks>
        /// <param name="targetFormat">The desired target audio format for the captured audio. If <see langword="null"/>, the audio will be
        /// keep origin format, but converted to 16-bit PCM format.</param>
        /// <param name="deviceId">The ID of the audio device to capture from. If set to <see langword="-1"/>, the default audio endpoint is used. Must be within
        /// the range of available devices.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="deviceId"/> is greater than or equal to the number of available audio devices.</exception>
        public AudioProvider(WaveFormat? targetFormat = null, int deviceId = -1)
        {
            recordingStream = new MyAudioStream { MaxBufferLength = 10 * MB };

            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (deviceId >= 0)
            {
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                if (deviceId >= devices.Count)
                    throw new ArgumentOutOfRangeException(nameof(deviceId), $"Device ID {deviceId} is out of range. Available devices: {devices.Count}");
                device = devices[deviceId];
                loopbackWaveIn = new WasapiLoopbackCapture(device);
            }

            loopbackWaveIn = new WasapiLoopbackCapture(device);
            loopbackWaveIn.DataAvailable += (s, a) =>
                recordingStream.Write(a.Buffer, 0, a.BytesRecorded);
            loopbackWaveIn.StartRecording();

            var floatSrc = new RawSourceWaveStream(recordingStream, loopbackWaveIn.WaveFormat);

            if (targetFormat is null)
            {
                pcmStream = (IWaveProvider?)floatSrc.ToSampleProvider().ToWaveProvider16(); //16bps audio is enough for 99% usage
            }
            else
            {
                var resamp = new MediaFoundationResampler(floatSrc, targetFormat) { ResamplerQuality = 60 };
                pcmStream = resamp;
            }

            isMp3Ready = pcmStream.WaveFormat.SampleRate <= 48000 && pcmStream.WaveFormat.BitsPerSample == 16;


            Console.WriteLine($"Listening on{(deviceId >= 0 ? "" : " default")} device {device.FriendlyName} @ {pcmStream.WaveFormat.SampleRate}Hz {pcmStream.WaveFormat.BitsPerSample}bps {pcmStream.WaveFormat.Channels}-channels");

            channels = pcmStream.WaveFormat.Channels;
            sampleRate = pcmStream.WaveFormat.SampleRate;
            bitRate = pcmStream.WaveFormat.BitsPerSample;

            waveThread = new Thread(WaveProcessor) { IsBackground = true };
            waveThread.Start();
        }

        public (Guid id, MyAudioStream stream) SubscribePcm(string ip = "", string name = "")
        {
            lock (locker)
            {
                var id = Guid.NewGuid();
                ListeningClients.Add(id);
                var pipe = new MyAudioStream { MaxBufferLength = 10 * MB };
                pcmSubscribers[id] = pipe;
                Console.WriteLine($"Client {id} ({name}) @ IPAddress:{ip} subscribed, now {listenClientsCount} listening.");
                return (id, pipe);
            }

        }

        public void UnsubscribePcm(Guid id)
        {
            lock (locker)
            {
                Tuple<string, string> name = new("", "");
                if (pcmSubscribers.TryRemove(id, out var pipe))
                {
                    pipe.Dispose();
                }
                SubscribedClients.Remove(id, out _);
                try
                {
                    ListeningClients = [.. ListeningClients.TakeWhile((c) => c != id)];
                }
                catch
                {

                }
                finally
                {
                    Console.WriteLine($"Client {id} unsubscribed, now {listenClientsCount} listening.");
                }
            }
        }

        private void WaveProcessor()
        {
            int block = pcmStream.WaveFormat.BlockAlign;
            var buf = new byte[block * 16];
            while (isRunning)
            {
                int n = pcmStream.Read(buf, 0, buf.Length);
                if (n > 0)
                {
                    foreach (var kv in pcmSubscribers)
                    {
                        try { kv.Value.Write(buf, 0, n); }
                        catch (Exception ex) { Console.WriteLine($"WARN: client pipe {kv.Key} throw {ex.GetType().Name} exception:{ex.Message}"); }
                    }
                }
                else
                {
                    Thread.Sleep(20);
                }
            }
        }

        public void Dispose()
        {
            isRunning = false;
            if (rawBuffer is not null) return;
            loopbackWaveIn.StopRecording();
            loopbackWaveIn.Dispose();
            foreach (var kv in pcmSubscribers) kv.Value.Dispose();
            recordingStream.Dispose();
        }
    }
}

