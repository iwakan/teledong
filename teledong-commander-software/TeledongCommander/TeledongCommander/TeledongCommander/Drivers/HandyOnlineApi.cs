﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace TeledongCommander;

public class HandyOnlineApi : OutputDevice
{
    public override bool IsConnected => successfullyConnected;
    public override bool HasError => !string.IsNullOrEmpty(errorMessage);

    public override string StatusText
    {
        get
        {
            if (successfullyConnected)
                return "Connected to [" + ConnectionKey + "]";
            else
                return "Not connected";
        }
    }

    public string ConnectionKey { get; set; } = "";

    const string baseApiUrl = "https://www.handyfeeling.com/api/handy/v2/";
    string? errorMessage = null;
    HttpClient httpClient;
    bool isClosed = false;
    bool successfullyConnected = false;
    Mutex criticalMessageLock = new Mutex();
    DateTime previousModeSetTime = DateTime.Now;
    DateTime previousCommandTime = DateTime.Now;
    double previousPosition = 1.0;

    public HandyOnlineApi()
    {
        Processor = new("handyOnlineApi");
        Processor.Output += Processor_Output;
        httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(2);
    }

    private void Processor_Output(object? sender, OutputEventArgs e)
    {
        var now = DateTime.Now;

        if (now - previousModeSetTime > TimeSpan.FromSeconds(5))
        {
            previousModeSetTime = now;
        }

        TimeSpan duration = TimeSpan.FromMilliseconds(Math.Clamp((now - previousCommandTime).TotalMilliseconds - 100, 100, e.Duration.TotalMilliseconds * 3));
        previousCommandTime = now;

        if (criticalMessageLock.WaitOne(0))
        {
            try
            {
                httpClient.CancelPendingRequests();
            }
            catch
            { }
            finally
            {
                criticalMessageLock.ReleaseMutex();
            }
        }

        using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hdsp/xpt"))
        {
            request.Headers.TryAddWithoutValidation("accept", "application/json");
            request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);

            var contentRaw = new
            {
                stopOnTarget = true,
                immediateResponse = true,
                duration = (int)duration.TotalMilliseconds,
                position = e.Position * 100.0,
            };
            request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

            try
            {
                httpClient.SendAsync(request);
                Debug.WriteLine("Sent to handy API: " + e.Position.ToString("N2") + " , " + contentRaw.duration /*+ ": " + result.ReasonPhrase*/);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed: " + ex.Message);
            }
        }
    }

    public override async Task Connect()
    {
        await SetMode();
    }

    public async Task<int> GetMode()
    {
        if (!criticalMessageLock.WaitOne(1000))
            return 0;

        try
        {
            using (var httpClient = new HttpClient())
            {
                using (var request = new HttpRequestMessage(new HttpMethod("GET"), baseApiUrl + "mode"))
                {
                    request.Headers.TryAddWithoutValidation("accept", "application/json");
                    request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);

                    var response = await httpClient.SendAsync(request);
                }
            }
            return 0;
        }
        catch
        { return 0; }
        finally
        {
            criticalMessageLock.ReleaseMutex();
        }
    }

    public async Task SetMode(int mode = 2)
    {
        if (!criticalMessageLock.WaitOne(1000))
            return;

        try
        {
            using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "mode"))
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);

                var contentRaw = new
                {
                    mode = mode
                };
                request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

                var response = await httpClient.SendAsync(request);

                if (response != null && response.IsSuccessStatusCode)
                    successfullyConnected = true;
            }
        }
        catch
        { }
        finally
        {
            criticalMessageLock.ReleaseMutex();
        }
    }

    public override Task Disconnect()
    {
        httpClient.CancelPendingRequests();
        return Task.CompletedTask;
    }
}
