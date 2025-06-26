using AudioClone.CoreCapture;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using System.Diagnostics;
using System.Net;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;

namespace libAudioCopy_Backend.Controllers;


[ApiController]
[Route("api/audio")]
public class AudioController : ControllerBase
{
    private AudioProvider _provider;

    public AudioController(AudioProvider provider)
    {
        string format = "";
        if ((format = Environment.GetEnvironmentVariable("AudioCopy_DefaultAudioQuality")) is not null && !string.IsNullOrWhiteSpace(format))
        {
            Console.WriteLine($"User override audio quality to: {format}");
            string[]? fmtArr = (format is not null && !string.IsNullOrWhiteSpace(format)) ? format.Split(',') : Array.Empty<string>();

            if (fmtArr.Length == 3 && fmtArr.All(item => int.TryParse(item, out int value) && value > 0))
            {
                _provider = new(
                    new WaveFormat(int.Parse(fmtArr[0]), int.Parse(fmtArr[1]), int.Parse(fmtArr[2])),
                    -1);
            }
            else
            {
                throw new ArgumentException("Invalid audio format. Ensure all values are positive integers.");
            }
        }
        else
        {
            _provider = provider;
            Console.WriteLine($"User not override audio quality, use default");

        }
    }

    private bool CheckToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentNullException("token", "Provide token.");
        Console.WriteLine("!Token");
        Thread.Sleep(50);
        var tk = (Console.ReadLine() ?? throw new ArgumentNullException()).Trim();
        Console.WriteLine($"Token {(token.Trim() == tk ? "is equals" : $"{tk} is not equals")} to user's token {token}");
        return token.Trim() == tk;
    }



    [HttpPut("SetCaptureOptions")]
    public async Task SetCaptureOptions(string? format = "", string token = "", CancellationToken ct = default)
    {
        if (!CheckToken(token))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsync("Unauthorized, please check your token.");
            return;
        }
        _provider.Dispose();
        string[]? fmtArr = (format is not null && !string.IsNullOrWhiteSpace(format)) ? format.Split(',') : Array.Empty<string>();

        _provider = new AudioProvider(
            (fmtArr.Length == 3) ? new WaveFormat(int.Parse(fmtArr[0]), int.Parse(fmtArr[1]), int.Parse(fmtArr[2])) : null,
            -1);

        return;

    }

    [HttpGet("GetAudioFormat")]
    public async Task GetAudioFormat(string token, CancellationToken ct)
    {
        if (!CheckToken(token))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsync("Unauthorized, please check your token.");
            return;
        }
        var format = _provider.PcmFormat;
        var json = new
        {
            format.SampleRate,
            format.BitsPerSample,
            format.Channels,
            _provider.isMp3Ready
        };
        Response.ContentType = "application/json";
        await Response.WriteAsJsonAsync(json, ct);
    }

    [HttpGet("mp3")]
    public async Task StreamMp3(string token, bool force = false, string clientName = "", CancellationToken ct = default)
    {
        if (!CheckToken(token))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsync("Unauthorized, please check your token.");
            return;
        }

        if (!_provider.isMp3Ready || force)
        {
            Response.StatusCode = StatusCodes.Status406NotAcceptable;
            await Response.WriteAsync("Enable resample or use force=true argument to continue get streamed MP3 audio.");
            return;
        }

        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.ContentType = "audio/mpeg";

        var (id, pipe) = _provider.SubscribePcm((HttpContext.Connection.RemoteIpAddress ?? IPAddress.Any).ToString().Split(':').Last(), clientName);

        try
        {
            using var mp3Writer = new LameMP3FileWriter(Response.Body, _provider.PcmFormat, 128);

            var buffer = new byte[_provider.PcmBlockAlign * 16];
            while (!ct.IsCancellationRequested)
            {
                int n = await pipe.ReadAsync(buffer, 0, buffer.Length, ct);
                if (n <= 0)
                {
                    await Task.Delay(20, ct);
                    continue;
                }

                mp3Writer.Write(buffer, 0, n);

                await Response.Body.FlushAsync(ct);
            }
        }
        finally
        {
            _provider.UnsubscribePcm(id);
        }
    }


    [HttpGet("wav")]
    public async Task StreamWav(string token, string clientName, CancellationToken ct)
    {
        if (!CheckToken(token))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsync("Unauthorized, please check your token.");
            return;
        }
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.ContentType = "audio/wav";
        Response.Headers["Accept-Ranges"] = "bytes";

        int channels = _provider.PcmFormat.Channels;
        int sampleRate = _provider.PcmFormat.SampleRate;
        int bitsSample = _provider.PcmFormat.BitsPerSample;
        int byteRate = sampleRate * channels * bitsSample / 8;
        short blockAlign = (short)(channels * bitsSample / 8);

        //wav header
        await Response.Body.WriteAsync(Encoding.ASCII.GetBytes("RIFF"), ct);
        await Response.Body.WriteAsync(BitConverter.GetBytes(uint.MaxValue), ct);
        await Response.Body.WriteAsync(Encoding.ASCII.GetBytes("WAVEfmt "), ct);
        await Response.Body.WriteAsync(BitConverter.GetBytes(16), ct);
        await Response.Body.WriteAsync(BitConverter.GetBytes((short)1), ct);
        await Response.Body.WriteAsync(BitConverter.GetBytes((short)channels), ct);
        await Response.Body.WriteAsync(BitConverter.GetBytes(sampleRate), ct);
        await Response.Body.WriteAsync(BitConverter.GetBytes(byteRate), ct);
        await Response.Body.WriteAsync(BitConverter.GetBytes(blockAlign), ct);
        await Response.Body.WriteAsync(BitConverter.GetBytes((short)bitsSample), ct);
        await Response.Body.WriteAsync(Encoding.ASCII.GetBytes("data"), ct);
        await Response.Body.WriteAsync(BitConverter.GetBytes(uint.MaxValue), ct);

        var (id, pipe) = _provider.SubscribePcm((HttpContext.Connection.RemoteIpAddress ?? IPAddress.Any).ToString().Split(':').Last(), clientName);
        try
        {
            var buffer = new byte[_provider.PcmBlockAlign * 16];
            while (!ct.IsCancellationRequested)
            {
                int n = await pipe.ReadAsync(buffer, 0, buffer.Length, ct);
                if (n <= 0) { await Task.Delay(20, ct); continue; }
                await Response.Body.WriteAsync(buffer, 0, n, ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        finally { _provider.UnsubscribePcm(id); }
    }

    [HttpGet("flac")]
    public async Task StreamFlac(string token, string clientName, CancellationToken ct)
    {
        if (!CheckToken(token))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsync("Unauthorized, please check your token.");
            return;
        }

        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.ContentType = "audio/ogg";

        var (id, pipe) = _provider.SubscribePcm((HttpContext.Connection.RemoteIpAddress ?? IPAddress.Any).ToString().Split(':').Last(), clientName);
        Process? flacProc = null;
        try
        {
            var i = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.CurrentDirectory,"..","flac.exe"),
                Arguments = "--best --ogg --force-raw-format --endian=little " +
                             $"--sign=signed --channels={_provider.channels} --bps={_provider.bitRate} --sample-rate={_provider.sampleRate} --stdout -",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            flacProc = Process.Start(i)!;

            _ = Task.Run(async () =>
            {
                var buf = new byte[_provider.PcmBlockAlign * 16];
                while (!ct.IsCancellationRequested)
                {
                    int n = await pipe.ReadAsync(buf, 0, buf.Length, ct);
                    if (n > 0) flacProc.StandardInput.BaseStream.Write(buf, 0, n);
                    else await Task.Delay(20, ct);
                }
                flacProc.StandardInput.Close();
            }, ct);

            var outStream = flacProc.StandardOutput.BaseStream;
            var obuf = new byte[8192];
            while (!ct.IsCancellationRequested)
            {
                int m = await outStream.ReadAsync(obuf, 0, obuf.Length, ct);
                if (m > 0) await Response.Body.WriteAsync(obuf, 0, m, ct);
                else await Task.Delay(20, ct);
            }
        }
        finally
        {
            flacProc?.Kill(true);
            _provider.UnsubscribePcm(id);
        }
    }

    [HttpGet("raw")]
    public async Task StreamRaw(string token, string clientName, CancellationToken ct = default)
    {
        if (!CheckToken(token))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsync("Unauthorized, please check your token.");
            return;
        }

        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.ContentType = "application/octet-stream";
        var (id, pipe) = _provider.SubscribePcm((HttpContext.Connection.RemoteIpAddress ?? IPAddress.Any).ToString().Split(':').Last(), clientName);
        await Task.Delay(500);
        try
        {
            byte[] buffer = new byte[_provider.PcmBlockAlign * 16];
            int n;
            while (!ct.IsCancellationRequested)
            {
                n = pipe.Read(buffer, 0, buffer.Length);
                if (n > 0)
                {
                    await Response.Body.WriteAsync(buffer.AsMemory(0, n), ct);
                }
                else
                {
                    await Task.Delay(20, ct);
                }
            }
        }
        finally
        {
            _provider.UnsubscribePcm(id);
        }
    }
}

