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
using System.Text.Json;
using Avalonia;

namespace TeledongCommander;

public class HandyStreamApi : OutputDevice
{
    public override bool IsStarted => successfullyConnected;

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
    public string ApiKey { get; set; } = "";

    const string baseApiUrl = "https://www.handyfeeling.com/api/handy-rest/v3/";
    HttpClient httpClient;
    bool isClosed = false;
    bool successfullyConnected = false;
    //Mutex criticalMessageLock = new Mutex();
    DateTime previousModeSetTime = DateTime.Now;
    Queue<HspPoint> buffer = new Queue<HspPoint>();
    DateTime startTime = DateTime.Now;
    DateTime lastBufferPushTime = DateTime.Now;
    int streamId = 100;
    string? apiAuthToken = null;
    bool isPlaying = false;
    bool hasInitedStart = false;
    int tailPointStreamIndex = 0;
    int millisecondsDiscrepancy = 0;
    bool hasAdjustedDiscrepancyTime = false;
    int millisecondsOffset => hasAdjustedDiscrepancyTime ? (int)Processor.FilterTime.TotalMilliseconds : 100;
    bool shouldRestart = false;
    bool alternatePointNoise = false;
    int numberOfBatchedPoints => (int)Math.Ceiling(Processor.FilterTime.TotalMilliseconds / 400.0);
    int[] previousPoints = { 0, 100, 2 };
    int[] previousCurrentPoints = { -1, -1, -1, -1, -1 };
    DateTime previousPointTime = DateTime.Now;
    int previousTime = 0;
    //int previousPoint = -1;
    BackgroundWorker? sseWorker = null;

    public HandyStreamApi() : base()
    {
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

        var flush = false;
        if (shouldRestart)
        {
            shouldRestart = false;
            /*await Stop();
            await Task.Delay(300);
            await Start();
            await Task.Delay(200);*/
            //flush = true;

            // Flush
            using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/flush"))
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

                var contentRaw = new
                {
                };
                request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

                try
                {
                    //Debug.WriteLine("Putting points: " + points.Count + " time: " + startTime.ToLongTimeString());
                    var response = await httpClient.SendAsync(request);
                    Debug.WriteLine("Flushing: " + response?.StatusCode);

                    if (response != null && response.IsSuccessStatusCode)
                    {
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Flushing failed: " + ex.Message);
                }
            }
            // Rewind
            using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/play"))
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

                var contentRaw = new
                {
                    startTime = previousTime + 100, //-(int)(DateTime.Now - benchmarkTime).TotalMilliseconds,
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
                {
                    ErrorMessage = "Failed to start playing...\nTry again.";
                    TriggerStatusChanged();
                    Debug.WriteLine("Failed to start playing: " + response?.ToString());
                }
            }
            //isPlaying = false;
            // Sync
            /*using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/synctime"))
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

                var contentRaw = new
                {
                    current_time = previousTime + 100, //-(int)(DateTime.Now - benchmarkTime).TotalMilliseconds,
                    server_time = ((DateTimeOffset)DateTimeOffset.Now.ToUniversalTime()).ToUnixTimeMilliseconds(),
                };
                request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

                try
                {
                    //Debug.WriteLine("Putting points: " + points.Count + " time: " + startTime.ToLongTimeString());
                    var response = await httpClient.SendAsync(request);
                    Debug.WriteLine("Synctime: " + response?.StatusCode);

                    if (response != null && response.IsSuccessStatusCode)
                    {
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Synctime failed: " + ex.Message);
                }
            }*/
        }
        shouldRestart = false;

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

                    //if (previousPoints[0] != x)
                    {
                        if (false)//if (previousPoints[1] == previousPoints[0] && previousPoints[2] == previousPoints[1])
                        {
                            // Add previous identical point if there has been a pause in motion and points have been skipped. Otherwise the reaction is too slow when resuming motion.
                            points.Add(new
                            {
                                t = (int)(previousPointTime - startTime).TotalMilliseconds + millisecondsOffset - millisecondsDiscrepancy,
                                x = previousPoints[0]
                            });
                            tailPointStreamIndex++;
                        }

                        // Add current point
                        points.Add(new
                        {
                            t = (int)(point.Time - startTime).TotalMilliseconds + millisecondsOffset - millisecondsDiscrepancy,
                            x = x
                        });
                        tailPointStreamIndex++;
                        //alternatePointNoise = !alternatePointNoise;
                    }

                    previousPoints[2] = previousPoints[1];
                    previousPoints[1] = previousPoints[0];
                    previousPoints[0] = x;
                    previousPointTime = point.Time;
                }
                while (buffer.Any());


                if (points.Count > 0)
                {
                    var benchmarkTime = DateTime.Now;
                    using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/add"))
                    {
                        request.Headers.TryAddWithoutValidation("accept", "application/json");
                        request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                        //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

                        var contentRaw = new
                        {
                            points = points.ToArray(),
                            flush = flush,
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
                                if (responseJson != null)
                                {
                                    if (responseJson.result != null)
                                    {
                                        if (isPlaying && !hasAdjustedDiscrepancyTime)
                                        {
                                            millisecondsDiscrepancy = responseJson.result.last_point_time - responseJson.result.current_time;// - 1000;
                                            Debug.WriteLine("Timing discrepancy: " + millisecondsDiscrepancy);
                                            hasAdjustedDiscrepancyTime = true;
                                        }

                                        var isStalled = true;
                                        for (int i = 0; i < 4; i++)
                                        {
                                            if (previousCurrentPoints[i] != previousCurrentPoints[i + 1] && previousCurrentPoints[i] > 0)
                                                isStalled = false;
                                            previousCurrentPoints[i] = previousCurrentPoints[i + 1];
                                        }
                                        if (previousCurrentPoints[4] != responseJson.result.current_point)
                                            isStalled = false;
                                        previousCurrentPoints[4] = responseJson.result.current_point;
                                        previousTime = responseJson.result.current_time;

                                        if (isStalled)
                                        {
                                            var update = ErrorMessage == null;
                                            ErrorMessage = "Stalled! Try increasing latency\nand/or reconnecting.";
                                            if (update)
                                                TriggerStatusChanged();
                                        }
                                        else
                                        {
                                            var update = ErrorMessage != null;
                                            ErrorMessage = null;
                                            if (update)
                                                TriggerStatusChanged();
                                        }

                                        if ((responseJson.result.max_points - responseJson.result.points) < 100)
                                        {
                                            // Reset connection because it gets buggy when reaching max points
                                            shouldRestart = true;
                                        }
                                    }
                                    else if (responseJson.error != null)
                                    {
                                        ErrorMessage = "Failed: " + responseJson.error.message;
                                        if (responseJson.error.code == 1001)
                                            ErrorMessage += ".\nMake sure it is updated to FW4, is online, and the connection key is correct.";
                                        TriggerStatusChanged();
                                    }
                                    Debug.WriteLine("Point put response: " + responseJson ?? "null");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (ex is not TaskCanceledException)
                            {
                                Debug.WriteLine("Failed: " + ex.Message);
                                ErrorMessage = "Failed to send positions";
                                TriggerStatusChanged();
                            }
                        }
                    }
                }


                if ((!isPlaying && (DateTime.Now - startTime) > TimeSpan.FromSeconds(1)))
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
                        {
                            ErrorMessage = "Failed to start playing...\nTry again.";
                            TriggerStatusChanged();
                            Debug.WriteLine("Failed to start playing: " + response?.ToString());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to send/process points to The Handy: " + ex.ToString());
            }

        }
    }

    public override async Task Start()
    {
        if (string.IsNullOrEmpty(ApiKey))
            ApiKey = App.UserData["HandyFw4BetaApiKey"] ?? "";

        await SetMode();
        await SetupStreaming();
        TriggerStatusChanged();
    }

    public async Task SetupStreaming()
    {
        //if (!criticalMessageLock.WaitOne(1000))
        //    return;

        successfullyConnected = false;
        buffer.Clear();
        //streamId += 1;
        tailPointStreamIndex = 0;
        for (int i = 0; i < 5; i++)
        {
            previousCurrentPoints[i] = -1;
        }

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

                    if (response == null)
                        throw new Exception("Failed to get auth response");

                    if (response.StatusCode == System.Net.HttpStatusCode.OK)
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
                    else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        ErrorMessage = "Unauthorized API key, check settings.";
                        TriggerStatusChanged();
                        return;
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
                    streamId = streamId++
                };
                request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

                var response = await httpClient.SendAsync(request);

                if (response != null && response.IsSuccessStatusCode)
                {
                    var responseText = await response.Content.ReadAsStringAsync();
                    Debug.WriteLine("Stream setup command response: " + responseText);

                    // Todo check for error code

                    successfullyConnected = true;
                    ErrorMessage = null;
                    TriggerStatusChanged();
                }
            }

#if DEBUG
            if (sseWorker == null)
            {
                sseWorker = new BackgroundWorker();
                sseWorker.DoWork += SseWorker_DoWork;
                sseWorker.RunWorkerAsync();
            }
#endif
        }
        catch (Exception ex)
        {
            ErrorMessage = "Failed to connect.";
            TriggerStatusChanged();
        }
        finally
        {
            //criticalMessageLock.ReleaseMutex();
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

        //while (true)
        { 
            try
            {
                eventSourceClient.Stream().Wait();
            }
            catch { }
        }

        sseWorker = null;
    }

    public async Task<int> GetMode()
    {
        //if (!criticalMessageLock.WaitOne(1000))
        //    return 0;

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
            //criticalMessageLock.ReleaseMutex();
        }
    }

    public async Task SetMode(int mode = 1) // Is this needed in APIv3? Is 1 even correct? 
    {
        //if (!criticalMessageLock.WaitOne(1000))
        //    return;

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
            ErrorMessage = "Failed to connect.";
            TriggerStatusChanged();
        }
        finally
        {
            //criticalMessageLock.ReleaseMutex();
        }
    }

    public override async Task Stop()
    {
        successfullyConnected = false;
        isPlaying = false;
        hasInitedStart = false;
        hasAdjustedDiscrepancyTime = false;
        httpClient.CancelPendingRequests();
        ErrorMessage = null;
        TriggerStatusChanged();

        try
        {
            using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "stop"))
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

                await httpClient.SendAsync(request);
            }
        }
        catch
        {
            return;
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
        public ErrorApiResponse? error { get; set; }

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


    public class ErrorApiResponse
    {
        public int code { get; set; }
        public string name { get; set; }
        public string message { get; set; }
        public bool connected { get; set; }
    }


}