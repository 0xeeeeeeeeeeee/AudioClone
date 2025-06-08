using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        public static ConcurrentBag<string> ListeningClients = new();

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
            var id = Guid.NewGuid();
            ListeningClients.Add($"{name}@{ip}");
            Console.WriteLine($"Client {id} ({name}) @ IPAddress:{ip} subscribed.");
            var pipe = new MyAudioStream { MaxBufferLength = 10 * MB };
            pcmSubscribers[id] = pipe;
            return (id, pipe);
        }

        public void UnsubscribePcm(Guid id)
        {
            Tuple<string, string> name = new("", "");
            if (pcmSubscribers.TryRemove(id, out var pipe))
            {
                Console.WriteLine($"Client {id} ({(SubscribedClients.TryGetValue(id, out name) ? name : "name unknown")}) unsubscribed.");
                pipe.Dispose();
            }
            SubscribedClients.Remove(id, out _);
            //ListeningClients = new ConcurrentBag<string>(ListeningClients.TakeWhile((c) => c != $"{name.Item2}@{name.Item1}"));
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

