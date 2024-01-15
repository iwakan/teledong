using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace TeledongCommander;

public class HandyStreamApi : OutputDevice
{
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromSeconds(0.8);

    public override bool IsStarted => successfullyConnected;
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

    private string connectionKey = "";
    public string ConnectionKey { get { return connectionKey; } set { if (connectionKey != value) { connectionKey = value; TriggerStatusChanged(); } } }

    const string baseApiUrl = "https://www.handyfeeling.com/api/handy-rest/v3/";
    string? errorMessage = null;
    HttpClient httpClient;
    bool isClosed = false;
    bool successfullyConnected = false;
    Mutex criticalMessageLock = new Mutex();
    DateTime previousModeSetTime = DateTime.Now;
    Queue<HspPoint> buffer = new Queue<HspPoint>();
    DateTime startTime = DateTime.Now;
    DateTime lastBufferPushTime = DateTime.Now;
    int streamId = 100;
    string ApiKey = "";
    bool isPlaying = false;
    int tailPointStreamIndex = 0;


    public HandyStreamApi() : base()
    {
        Processor.Output += Processor_Output;
        httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(2);
    }

    private async void Processor_Output(object? sender, OutputEventArgs e)
    {
        if (!IsStarted)
            return;

        var now = DateTime.Now;

        if (!isPlaying && !buffer.Any())
        {
            lastBufferPushTime = now;
            startTime = now;
        }

        // Record point in buffer
        buffer.Enqueue(new HspPoint() { Position = e.Position, Time = now });

        if (now >= lastBufferPushTime + BufferTime)
        {
            try
            {
                // Push buffer to stream if enough time has passed
                lastBufferPushTime = now;
                var points = new List<object>();
                do
                {
                    var point = buffer.Dequeue();
                    points.Add(new
                    {
                        t = (int)(point.Time - startTime).TotalMilliseconds,
                        x = (int)Math.Round(point.Position * 100)
                    });
                    tailPointStreamIndex++;
                }
                while (buffer.Any());

                using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/add"))
                {
                    request.Headers.TryAddWithoutValidation("accept", "application/json");
                    request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                    request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);

                    var contentRaw = new
                    {
                        points = points.ToArray(),
                        flush = false,
                        tailPointStreamIndex = tailPointStreamIndex,

                    };
                    request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                    request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

                    try
                    {
                        var response = await httpClient.SendAsync(request);

                        if (response != null && response.IsSuccessStatusCode)
                        {
                            Debug.WriteLine(await response.Content.ReadAsStringAsync());
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Failed: " + ex.Message);
                        errorMessage = "Failed to send positions";
                        TriggerStatusChanged();

                    }
                }


                if (!isPlaying)
                {
                    using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/play"))
                    {
                        request.Headers.TryAddWithoutValidation("accept", "application/json");
                        request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                        request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);

                        var contentRaw = new
                        {
                            startTime = (int)(startTime-now).TotalMilliseconds,
                            serverTime = ((DateTimeOffset)startTime.ToUniversalTime()).ToUnixTimeMilliseconds(),
                            playbackRate = 1.0,
                            loop = false,
                        };
                        request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

                        var response = await httpClient.SendAsync(request);

                        if (response != null && response.IsSuccessStatusCode)
                        {
                            successfullyConnected = true;
                            Debug.WriteLine("START PLAYING");
                            Debug.WriteLine(await response.Content.ReadAsStringAsync());
                            isPlaying = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }
    }

    public override async Task Start()
    {
        await SetMode();
        await SetupStreaming();
        TriggerStatusChanged();
    }

    public async Task SetupStreaming()
    {
        if (!criticalMessageLock.WaitOne(1000))
            return;

        successfullyConnected = false;
        buffer.Clear();
        //streamId += 1;
        tailPointStreamIndex = 0;

        try
        {
            await Stop();

            using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/setup"))
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);

                var contentRaw = new
                {
                    streamId = streamId
                };
                request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

                var response = await httpClient.SendAsync(request);

                if (response != null && response.IsSuccessStatusCode)
                {
                    successfullyConnected = true;
                    Debug.WriteLine(await response.Content.ReadAsStringAsync());
                }
            }
        }
        catch (Exception ex)
        {
            errorMessage = "Failed to connect.";
        }
        finally
        {
            criticalMessageLock.ReleaseMutex();
        }
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
                    request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);

                    var response = await httpClient.SendAsync(request);
                }
            }
            return 0;
        }
        catch
        { 
            return 0; 
        }
        finally
        {
            criticalMessageLock.ReleaseMutex();
        }
    }

    public async Task SetMode(int mode = 1)
    {
        if (!criticalMessageLock.WaitOne(1000))
            return;

        try
        {
            using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "mode"))
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);

                var contentRaw = new
                {
                    mode = mode
                };
                request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

                /*var response = await httpClient.SendAsync(request);

                if (response != null && response.IsSuccessStatusCode)
                {
                    successfullyConnected = true;
                }*/
            }
        }
        catch (Exception ex)
        {
            errorMessage = "Failed to connect.";
        }
        finally
        {
            criticalMessageLock.ReleaseMutex();
        }
    }

    public override Task Stop()
    {
        successfullyConnected = false;
        isPlaying = false;
        httpClient.CancelPendingRequests();
        TriggerStatusChanged();

        try
        {
            using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "stop"))
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);

                return httpClient.SendAsync(request);
            }
        }
        catch
        {
            return Task.CompletedTask;
        }
    }

    struct HspPoint
    {
        public double Position; // From 0 to 1
        public DateTime Time;
    }
}