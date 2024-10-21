using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TeledongCommander
{
    /// <summary>
    /// Class for recording a funscript in real-time
    /// </summary>
    public class FunscriptRecorder : OutputDevice 
    {
        public override bool IsStarted => IsRecording;
        public override string StatusText => (IsRecording) ? $"Recording: {RecordingDuration.ToString(@"mm\:ss")}" : "Not recording.";

        public string OutputPath { get; set; }
        public bool IsRecording { get; set; } = false;
        public TimeSpan RecordingDuration => IsRecording ? (DateTime.UtcNow - startTime) : TimeSpan.Zero;
        public TimeSpan TimeShiftForward { get; set; } = TimeSpan.Zero;
        public double MaximumRange { get; set; } = 1.0;
        public double MinimumRange { get; set; } = 0.0;

        DateTime startTime = DateTime.UtcNow;
        List<FunscriptPoint> points = new List<FunscriptPoint>();

        public FunscriptRecorder() : base()
        {
            OutputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"teledong_{DateTime.UtcNow.ToShortDateString()}.funscript");

            Processor.Output += Processor_Output;
        }

        private void Processor_Output(object? sender, OutputEventArgs e)
        {
            if (!IsRecording)
                return;

            points.Add(new FunscriptPoint(e.Position, DateTime.UtcNow - startTime));
        }

        public override async Task Start()
        {
            StartRecording();
        }

        public override async Task Stop()
        {
            ErrorMessage = null;
            await StopRecording();
        }

        /// <summary>
        /// Initializes a recording. After calling this you can start calling PutPosition() for new points.
        /// </summary>
        public void StartRecording()
        {
            if (string.IsNullOrEmpty(OutputPath))
                return; //Todo notify error

            points.Clear();
            startTime = DateTime.UtcNow;
            IsRecording = true;
            TriggerStatusChanged();
        }

        /// <summary>
        /// Stops and saves the ongoing recording to an auto-generated filename in the folder OutputDirectory.
        /// </summary>
        public async Task StopRecording()
        {
            IsRecording = false;

            if (points.Count == 0)
                return;

            // Todo filter points with RamerDouglasPeucker

            var actionsJson = new JsonArray();

            foreach (FunscriptPoint point in points)
            {
                actionsJson.Add(new JsonObject()
                {
                    ["pos"] = (int)Math.Round((MinimumRange + point.Position * (MaximumRange - MinimumRange)) * 99.0),
                    ["at"] = (int)(point.Time + TimeShiftForward).TotalMilliseconds
                });
            }

            var json = new JsonObject()
            {
                ["version"] = "1.0",
                ["metadata"] = new JsonObject()
                {
                    ["title"] = "Teledong script",
                    ["duration"] = (int)(points.Last().Time + TimeShiftForward).TotalMilliseconds,
                    ["range"] = 100
                },
                ["actions"] = actionsJson
            };

            var path = OutputPath;
            int suffix = 1;
            while (File.Exists(path))
            {
                path = path.Replace(((suffix == 1) ? "" : "-" + suffix.ToString()) + ".funscript", "-" + (++suffix).ToString() + ".funscript");
            }

            using (var fileStream = File.OpenWrite(path))
            {
                var jsonWriter = new Utf8JsonWriter(fileStream, new JsonWriterOptions() { Indented = false });
                json.WriteTo(jsonWriter);
                await jsonWriter.FlushAsync();
                await fileStream.FlushAsync();
            }

            TriggerStatusChanged();
        }
    }

    public struct FunscriptPoint
    {
        public FunscriptPoint(double position, TimeSpan time)
        {
            Position = position;
            Time = time;
        }

        public double Position { get; } // From 0 to 1
        public TimeSpan Time { get; }
    }
}
