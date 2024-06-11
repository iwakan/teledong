using LaborAI.EventSourceClient.DTOs;
using LaborAI.EventSourceClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.ComponentModel;

namespace TeledongCommander;

public class HandyStreamApi : OutputDevice
{
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
    string? apiAuthToken = null;
    bool isPlaying = false;
    bool hasInitedStart = false;
    int tailPointStreamIndex = 0;
    int millisecondsDiscrepancy = 0;
    bool hasAdjustedDiscrepancyTime = false;
    int millisecondsOffset => hasAdjustedDiscrepancyTime ? (int)Processor.FilterTime.TotalMilliseconds : 100;
    bool flushNext = false;
    bool alternatePointNoise = false;
    int numberOfBatchedPoints => (int)Math.Ceiling(Processor.FilterTime.TotalMilliseconds / 400.0);
    int[] previousPoints = { 0, 100, 2 };
    int previousPoint = -1;
    BackgroundWorker? sseWorker = null;

    public HandyStreamApi() : base()
    {
        ApiKey = App.UserData["HandyFw4BetaApiKey"];

        Processor.SkipFiltering = true;
        Processor.Output += Processor_Output;
        Processor.PeakMotionMode = false;
        Processor.FilterTime = TimeSpan.FromMilliseconds(1000);
        httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    private async void Processor_Output(object? sender, OutputEventArgs e)
    {
        if (!IsStarted)
            return;

        var now = DateTime.Now;

        if (!hasInitedStart)
        {
            lastBufferPushTime = now;
            startTime = now;
            hasInitedStart = true;
        }

        /*if (!isPlaying && !buffer.Any())
        {
            lastBufferPushTime = now;
            startTime = now;
        }*/

        // Record point in buffer
        buffer.Enqueue(new HspPoint() { Position = e.Position, Time = now });

        if (buffer.Count >= numberOfBatchedPoints) //now >= lastBufferPushTime )//+ Processor.FilterTime)
        {
            try
            {
                // Push buffer to stream if enough time has passed
                lastBufferPushTime = now;
                //int firstPointTime = -10000;
                var points = new List<object>();
                do
                {
                    var point = buffer.Dequeue();
                    var x = Math.Clamp((int)Math.Round(point.Position * 100) + (alternatePointNoise ? 1 : 0), 0, 100);

                    /*if (previousPoints[2] > previousPoints[1] && previousPoints[1] == previousPoints[0] && previousPoints[0] == x)
                    {
                        x += 1;
                        if (x > 100)
                            x = 100;
                    }
                    else
                    {
                        previousPoints[2] = previousPoints[1];
                        previousPoints[1] = previousPoints[0];
                        previousPoints[0] = x;
                    }*/
                    //if (firstPointTime == -10000)
                    //    firstPointTime = (int)(point.Time - startTime).TotalMilliseconds + millisecondsOffset;

                    //if (previousPoint != x)
                    {
                        points.Add(new
                        {
                            t = (int)(point.Time - startTime).TotalMilliseconds + millisecondsOffset - millisecondsDiscrepancy,
                            x = x
                        });
                        tailPointStreamIndex++;
                        //alternatePointNoise = !alternatePointNoise;
                        previousPoint = x;
                    }
                }
                while (buffer.Any());

                //if (points.Count > 0)
                {
                    var benchmarkTime = DateTime.Now;
                    using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/add"))
                    {
                        request.Headers.TryAddWithoutValidation("accept", "application/json");
                        request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                        //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

                        if (flushNext)
                            tailPointStreamIndex = points.Count;

                        var contentRaw = new
                        {
                            points = points.ToArray(),
                            flush = flushNext,
                            tailPointStreamIndex = tailPointStreamIndex,
                        };
                        request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

                        try
                        {
                            //Debug.WriteLine("Putting points: " + points.Count + " time: " + startTime.ToLongTimeString());
                            var response = await httpClient.SendAsync(request);

                            Debug.WriteLine("API roundtrip latency: " + (DateTime.Now - benchmarkTime).TotalMilliseconds);

                            if (response != null && response.IsSuccessStatusCode)
                            {
                                var responseText = await response.Content.ReadAsStringAsync();
                                var responseJson = System.Text.Json.JsonSerializer.Deserialize<PutPointsApiResponse>(responseText);
                                if (responseJson != null && responseJson.result != null)
                                {
                                    //millisecondsDiscrepancy = (responseJson.result.current_time - firstPointTime);
                                    //millisecondsOffset += millisecondsDiscrepancy;
                                    //if (responseJson.result.points + 100 > responseJson.result.max_points)
                                    //    flushNext = true;
                                    if (isPlaying && !hasAdjustedDiscrepancyTime)
                                    {
                                        millisecondsDiscrepancy = responseJson.result.last_point_time - responseJson.result.current_time;// - 1000;
                                        Debug.WriteLine("Timing discrepancy: " + millisecondsDiscrepancy);
                                        hasAdjustedDiscrepancyTime = true;
                                    }
                                }
                                Debug.WriteLine("Point put response: " + responseJson);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("Failed: " + ex.Message);
                            errorMessage = "Failed to send positions";
                            TriggerStatusChanged();
                        }
                    }
                }


                if ((!isPlaying && (DateTime.Now - startTime) > TimeSpan.FromSeconds(1)) || flushNext)
                {
                    using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/play"))
                    {
                        request.Headers.TryAddWithoutValidation("accept", "application/json");
                        request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                        //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

                        var contentRaw = new
                        {
                            startTime = 0, //-(int)(DateTime.Now - benchmarkTime).TotalMilliseconds,
                            serverTime = ((DateTimeOffset)DateTimeOffset.Now.ToUniversalTime()).ToUnixTimeMilliseconds(),
                            playbackRate = 1.0,
                            loop = false,
                        };
                        request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

                        var response = await httpClient.SendAsync(request);

                        if (response != null && response.IsSuccessStatusCode)
                        {
                            successfullyConnected = true;
                            Debug.WriteLine("START PLAYING " + startTime.ToLongTimeString());
                            Debug.WriteLine(await response.Content.ReadAsStringAsync());
                            isPlaying = true;
                        }
                        else
                            Debug.WriteLine("Failed to start playing: " + response?.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to send/process points to The Handy: " + ex.ToString());
            }

            flushNext = false;
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
            if (string.IsNullOrEmpty(apiAuthToken))
            {
                Debug.WriteLine("Authenticating...");
                using (var request = new HttpRequestMessage(new HttpMethod("GET"), baseApiUrl + "auth/token/issue"))
                {
                    request.Headers.TryAddWithoutValidation("accept", "application/json");
                    //request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                    request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);

                    var contentRaw = new
                    {
                        ck = ConnectionKey
                    };
                    request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                    request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

                    var response = await httpClient.SendAsync(request);

                    if (response != null)
                    {
                        var responseText = await response.Content.ReadAsStringAsync();
                        var responseJson = System.Text.Json.JsonSerializer.Deserialize<AuthenticationApiResponse>(responseText);
                        if (responseJson != null)
                        {
                            Debug.WriteLine("Authentication response: " + responseText);
                            //if (response.IsSuccessStatusCode)
                            apiAuthToken = responseJson.token;
                        }
                    }
                }
            }


            await Stop();

            using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/setup"))
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

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
                    Debug.WriteLine("Stream setup command response: " + await response.Content.ReadAsStringAsync());
                }
            }

            if (sseWorker == null)
            {
                sseWorker = new BackgroundWorker();
                sseWorker.DoWork += SseWorker_DoWork;
                sseWorker.RunWorkerAsync();
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

    private void SseWorker_DoWork(object? sender, DoWorkEventArgs e)
    {
        var eventSourceClient = new EventSourceClient(baseApiUrl + "sse", new HttpClient(), null, new EventSourceExtraOptions()
        {
            Headers = new Dictionary<string, string>()
            {
                ["X-Connection-Key"] = ConnectionKey,
                //["X-Api-Key"] = ApiKey
                ["Authorization"] = "Bearer " + apiAuthToken
            }
        });

        eventSourceClient.EventReceived += (sender, e) =>
        {
            Debug.WriteLine($"SSE Received Event: {e.Type}");
            Debug.WriteLine($"SSE Data: {e.Data}");
            Debug.WriteLine($"SSE ID: {e.Id}");
            if (e.Retry.HasValue)
            {
                Debug.WriteLine($"SSE Retry: {e.Retry.Value}");
            }
        };

        eventSourceClient.StateChanged += (sender, e) => Debug.WriteLine($"SSE State Changed: {e.ReadyState}");

        eventSourceClient.Stream().Wait();

        sseWorker = null;
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
                    //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

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
                //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

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
        hasInitedStart = false;
        httpClient.CancelPendingRequests();
        TriggerStatusChanged();

        try
        {
            using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "stop"))
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

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

    protected class PutPointsApiResponse
    {
        public PutPointsApiResponseResult? result { get; set; }

        public override string ToString()
        {
            return result?.ToString() ?? "Empty";
        }
    }

    protected class PutPointsApiResponseResult
    {
        public int points { get; set; }
        public int max_points { get; set; }
        public int current_point { get; set; }
        public int current_time { get; set; }
        public int first_point_time { get; set; }
        public int last_point_time { get; set; }
        public int tail_point_stream_index { get; set; }

        public override string ToString()
        {
            return $"points={points}, current_point={current_point}, current_time={current_time}, last_point_time={last_point_time}, tail_point_stream_index={tail_point_stream_index}";
        }
    }


    public class AuthenticationApiResponse
    {
        public string token_id { get; set; }
        public string token { get; set; }
        public string renew { get; set; }
    }

}