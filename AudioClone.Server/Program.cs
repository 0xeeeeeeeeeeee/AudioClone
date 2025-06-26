using AudioClone.CoreCapture;
using libAudioCopy_Backend;
using libAudioCopy_Backend.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

internal class Program
{
    private static void Main(string[] args)
    {
        Thread.Sleep(250); //等待日志采集启动

        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Mvc.Infrastructure.ControllerActionInvoker", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Mvc.Infrastructure.ObjectResultExecutor", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Mvc.StatusCodeResult", LogLevel.Information);


        builder.Services.AddSingleton<AudioProvider>();


        builder.Services.AddControllers();

        builder.Services.Configure<KestrelServerOptions>(opts => opts.AllowSynchronousIO = true);
        builder.Services.Configure<IISServerOptions>(opts => opts.AllowSynchronousIO = true);
        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
        }

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(async context =>
                {
                    var contextFeature = context.Features.Get<IExceptionHandlerFeature>();
                    if (contextFeature != null)
                    {
                        var ex = contextFeature.Error;
                        Console.Error.WriteLine(LogException(ex));
                        context.Response.StatusCode = 500;
                        context.Response.ContentType = "text/plain";
                        await context.Response.WriteAsync($"A {contextFeature.Error.GetType().Name} exception happens: {contextFeature.Error.Message}. For additional information, check the log.");
                    }
                });
            });
        }
        else
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.MapControllers();
        try
        {
            var listenMonitorCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                int belowZeroCount = 0;
                while (!listenMonitorCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        long clientCount = AudioProvider.listenClientsCount;
                        if (clientCount <= 0)
                        {
                            belowZeroCount++;
                            Console.WriteLine($"no clients listening for {belowZeroCount / 2} minutes");
                        }
                        else
                        {
                            belowZeroCount = 0;
                            if(sw.Elapsed.TotalMinutes > 3)
                            {
                                Console.WriteLine($"{clientCount} clients listening.");
                                sw.Restart();
                            }
                        }
                        if (belowZeroCount >= 6)
                        {
                            Console.WriteLine("No any connection in 3 minutes, exit.");
                            Environment.Exit(0);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"An {ex.GetType().Name} happens:{ex.Message}");
                    }
                    await Task.Delay(TimeSpan.FromMinutes(0.5), listenMonitorCts.Token);
                }
            }, listenMonitorCts.Token);
            
            app.Run();

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(LogException(ex));
        }
    }



    public static string LogException(Exception ex)
    {
        string innerExceptionInfo = "None";
        if (ex.InnerException != null)
        {
            innerExceptionInfo =
$"""
Type: {ex.InnerException.GetType().Name}                        
Message: {ex.InnerException.Message}
StackTrace:
{ex.InnerException.StackTrace}

""";
        }
        return
$"""
Exception type: {ex.GetType().Name}
Message: {ex.Message}
StackTrace:
{ex.StackTrace}
                            
From:{(ex.TargetSite is not null ? ex.TargetSite.ToString() : "unknown")}
InnerException:
{innerExceptionInfo}
                            
Exception data:
{string.Join("\r\n", ex.Data.Cast<System.Collections.DictionaryEntry>().Select(k => $"{k.Key} : {k.Value}"))}
                            
""";
            
    }

}
