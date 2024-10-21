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
    DateTime previousModeSetTime = DateTime.UtcNow;
    Queue<HspPoint> buffer = new Queue<HspPoint>();
    DateTime startTime = DateTime.UtcNow;
    DateTime lastBufferPushTime = DateTime.UtcNow;
    int streamId = 100;
    string? apiAuthToken = null;
    bool isPlaying = false;
    bool hasInitedStart = false;
    bool hasStopped = true;
    int tailPointStreamIndex = 0;
    int millisecondsDiscrepancy = 0;
    bool hasAdjustedDiscrepancyTime = false;
    int millisecondsOffset => (int)Processor.FilterTime.TotalMilliseconds; //hasAdjustedDiscrepancyTime ? (int)Processor.FilterTime.TotalMilliseconds : 100;
    bool shouldRestart = false;
    bool alternatePointNoise = false;
    int numberOfBatchedPoints => (int)Math.Ceiling(millisecondsOffset / 500.0);
    int[] previousPoints = { 0, 100, 2 };
    int[] previousCurrentPoints = { -1, -1, -1, -1, -1 };
    DateTime previousPointTime = DateTime.UtcNow;
    int previousTime = 0;
    long clientServerTimeOffset = 100;
    //int previousPoint = -1;
    BackgroundWorker? sseWorker = null;

    public HandyStreamApi() : base()
    {
        Processor.SkipFiltering = true;
        Processor.Output += Processor_Output;
        Processor.PeakMotionMode = false;
        Processor.FilterTime = TimeSpan.FromMilliseconds(400);
        httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(6);
    }

    // Receive points from input device
    private async void Processor_Output(object? sender, OutputEventArgs e)
    {
        if (!IsStarted )//&& !hasStopped)
            return;

        var now = DateTime.UtcNow;

        var flush = false;
        if (shouldRestart)
        {
            shouldRestart = false;

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
            isPlaying = false;
            /*using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/play"))
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

                var contentRaw = new
                {
                    startTime = DateTime.UtcNow - startTime, //-(int)(DateTime.UtcNow - benchmarkTime).TotalMilliseconds,
                    serverTime = ((DateTimeOffset)DateTimeOffset.UtcNow.ToUniversalTime()).ToUnixTimeMilliseconds() + clientServerTimeOffset,
                    playbackRate = 1.0,
                    loop = false,
                };
                request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

                var response = await httpClient.SendAsync(request);

                if (response != null && response.IsSuccessStatusCode)
                {
                    Debug.WriteLine("START PLAYING " + startTime.ToLongTimeString());
                   // if (!hasStopped)
                    {
                        //successfullyConnected = true;
                        Debug.WriteLine(await response.Content.ReadAsStringAsync());
                        isPlaying = true;
                    }
                }
                else
                {
                    ErrorMessage = "Failed to start playing...\nTry again.";
                    TriggerStatusChanged();
                    Debug.WriteLine("Failed to start playing: " + response?.ToString());
                }
            }*/
        }

        if (!hasInitedStart)
        {
            lastBufferPushTime = now;
            startTime = now;
            hasInitedStart = true;
            flush = true;
        }

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

                    //if (previousPoints[0] != x)
                    {
                        if (false)//if (previousPoints[1] == previousPoints[0] && previousPoints[2] == previousPoints[1])
                        {
                            // Add previous identical point if there has been a pause in motion and points have been skipped. Otherwise the reaction is too slow when resuming motion.
                            points.Add(new
                            {
                                t = (int)(previousPointTime - startTime).TotalMilliseconds + millisecondsOffset, // - millisecondsDiscrepancy,
                                x = previousPoints[0]
                            });
                            tailPointStreamIndex++;
                        }

                        // Add current point
                        points.Add(new
                        {
                            t = (int)(point.Time - startTime).TotalMilliseconds + millisecondsOffset, // - millisecondsDiscrepancy,
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
                    //var benchmarkTime = DateTime.UtcNow;
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

                            //Debug.WriteLine("API roundtrip latency: " + (DateTime.UtcNow - benchmarkTime).TotalMilliseconds);

                            if (response != null && response.IsSuccessStatusCode)
                            {
                                var responseText = await response.Content.ReadAsStringAsync();
                                var responseJson = System.Text.Json.JsonSerializer.Deserialize<PutPointsApiResponse>(responseText);
                                if (responseJson != null)
                                {
                                    if (responseJson.result != null)
                                    {
                                        /*if (isPlaying && !hasAdjustedDiscrepancyTime)
                                        {
                                            millisecondsDiscrepancy = responseJson.result.last_point_time - responseJson.result.current_time;// - 1000;
                                            Debug.WriteLine("Timing discrepancy: " + millisecondsDiscrepancy);
                                            hasAdjustedDiscrepancyTime = true;
                                        }*/

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
                                            // Flush and reset point buffer because it gets buggy when reaching max points
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


                if ((!isPlaying))// && (DateTime.UtcNow - startTime) > TimeSpan.FromSeconds(1)))
                {
                    isPlaying = true;
                    using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/play"))
                    {
                        request.Headers.TryAddWithoutValidation("accept", "application/json");
                        request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                        //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

                        var contentRaw = new
                        {
                            startTime = (long)(now - startTime).TotalMilliseconds, //-(int)(DateTime.UtcNow - benchmarkTime).TotalMilliseconds,
                            serverTime = ((DateTimeOffset)DateTimeOffset.UtcNow.ToUniversalTime()).ToUnixTimeMilliseconds() + clientServerTimeOffset,
                            playbackRate = 1.0,
                            loop = false,
                        };
                        request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

                        var response = await httpClient.SendAsync(request);

                        if (response != null && response.IsSuccessStatusCode)
                        {
                            Debug.WriteLine("START PLAYING " + startTime.ToLongTimeString());
                            //if (!hasStopped)
                            {
                                //successfullyConnected = true;
                                Debug.WriteLine(await response.Content.ReadAsStringAsync());
                                //isPlaying = true;
                            }
                        }
                        else
                        {
                            isPlaying = false;
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

    // https://ohdoki.notion.site/Handy-API-v3-ea6c47749f854fbcabcc40c729ea6df4 chapter "Synchronized protocols"
    public async Task<long> GetClientServerTimeOffset()
    {
        const int numSamples = 20;
        int timeout = 10;
        var stopwatch = new Stopwatch();
        long offsetTimeSum = 0;

        for (int i = 0; i < numSamples; i++)
        {
            using (var request = new HttpRequestMessage(new HttpMethod("GET"), baseApiUrl + "servertime"))
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

                /*var contentRaw = new
                {
                    ck = ConnectionKey
                };
                request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(contentRaw));
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");*/

                stopwatch.Restart();
                var response = await httpClient.SendAsync(request);

                if (response == null)
                {
                    if (timeout-- <= 0)
                        throw new Exception("Failed to get servertime");
                    i--;
                    continue;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    var responseText = await response.Content.ReadAsStringAsync();
                    var responseJson = System.Text.Json.JsonSerializer.Deserialize<ServertimeApiResponse>(responseText);
                    if (responseJson != null && responseJson.server_time > 0)
                    {
                        stopwatch.Stop();
                        var clientTime = ((DateTimeOffset)DateTimeOffset.UtcNow.ToUniversalTime()).ToUnixTimeMilliseconds();
                        var rountripDelay = stopwatch.ElapsedMilliseconds;
                        Debug.WriteLine("Servertime response: " + responseText);
                        //if (response.IsSuccessStatusCode)
                        var serverTime = responseJson.server_time;
                        var estimatedServerReceiveTime = serverTime + rountripDelay / 2;
                        var timeOffset = estimatedServerReceiveTime - clientTime;
                        offsetTimeSum += timeOffset;
                    }
                    else
                    {
                        if (timeout-- <= 0)
                            throw new Exception("Failed to get servertime");
                        i--;
                        continue;
                    }
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new Exception("Unauthorized API key");
                }
            }
        }

        var offset = offsetTimeSum / numSamples;
        Debug.WriteLine("CLIENT-SERVER OFFSET: " + offset.ToString());
        return offset;
    }

    public override async Task Start()
    {
        if (string.IsNullOrEmpty(ConnectionKey))
        {
            ErrorMessage = "Please enter a connection key before connecting.";
            TriggerStatusChanged();
            return;
        }

        if (string.IsNullOrEmpty(ApiKey))
            ApiKey = App.UserData["HandyFw4BetaApiKey"] ?? ""; // Not used right now, using public Handy App ID instead, see Authenticate()

        //await SetMode();

        try
        {
            clientServerTimeOffset = await GetClientServerTimeOffset();
            await SetupStreaming();
            TriggerStatusChanged();
        }
        catch (Exception e)
        {
            ErrorMessage = "Something went wrong when connecting: " + e.Message;
            TriggerStatusChanged();
        }
    }

    public async Task Authenticate()
    {
        apiAuthToken = "6TpU0euyxpYGZFoeQ~AimuZl__kU57U~";
        return;

        /*if (string.IsNullOrEmpty(apiAuthToken))
        {
            Debug.WriteLine("Authenticating...");
            using (var request = new HttpRequestMessage(new HttpMethod("GET"), baseApiUrl + "auth/token/issue"))
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                //request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);

                var contentRaw = new
                {
                    ck = ConnectionKey,
                    ttl = 3600*6
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
                else
                {
                    ErrorMessage = "Error when connecting: " + response.ReasonPhrase + ", " + await response.Content.ReadAsStringAsync() ?? "";
                    TriggerStatusChanged();
                    return;
                }
            }
        }*/
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
                await Authenticate();
            if (string.IsNullOrEmpty(apiAuthToken))
                return;

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
                    //hasStopped = false;
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
        //hasStopped = true;
        hasInitedStart = false;
        hasAdjustedDiscrepancyTime = false;
        //httpClient.CancelPendingRequests();
        ErrorMessage = null;
        TriggerStatusChanged();

        try
        {
            // Repeat to make sure there is no race between incoming points
            for (int i = 0; i < 2; i++)
            {
                using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/stop"))
                {
                    request.Headers.TryAddWithoutValidation("accept", "application/json");
                    request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                    //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

                    await httpClient.SendAsync(request);

                }
                using (var request = new HttpRequestMessage(new HttpMethod("PUT"), baseApiUrl + "hsp/flush"))
                {
                    request.Headers.TryAddWithoutValidation("accept", "application/json");
                    request.Headers.TryAddWithoutValidation("X-Connection-Key", ConnectionKey);
                    //request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiAuthToken);

                    await httpClient.SendAsync(request);

                }
                await Task.Delay(100);
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


    public class ServertimeApiResponse
    {
        public long server_time { get; set; }
    }



}