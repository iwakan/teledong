using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TeledongCommander.ViewModels;

namespace TeledongCommander
{
    // Class that takes raw points as input, and does stuff like add delay, reduce number of points, etc, before outputting (via events)
    public class OutputProcessor
    {
        public double FilterEpsilon { get; set; } = 0.05;
        public TimeSpan FilterTime { get; set; } = TimeSpan.FromMilliseconds(300);

        readonly string settingsId;
        readonly List<StrokerPoint> inputPointBuffer = new List<StrokerPoint>();
        readonly Queue<StrokerPoint> outputPointBuffer = new Queue<StrokerPoint>();
        readonly DateTime referenceTime = DateTime.Now;
        DateTime lastWriteTime = DateTime.Now;

        public event EventHandler<OutputEventArgs>? Output;

        public OutputProcessor(string settingsId) 
        {
            this.settingsId = settingsId;
        }

        // This will send the current position (from 0 to 1) to processing, and trigger the Output event if a new position is ready to be sent to the device.
        // Should be called as often and soon as possible, such as every time you have a new reading from the sensor.
        public void PutPositionAndProcessOutput(double position)
        {
            var now = DateTime.Now - referenceTime;

            inputPointBuffer.Add(new StrokerPoint(position, now));

            // Filter and queue
            if ((now - inputPointBuffer.First().Time) > FilterTime)
            {
                List<StrokerPoint> processedPointBuffer = RamerDouglasPeuckerNetV2.RamerDouglasPeucker.Reduce(inputPointBuffer.ToList(), FilterEpsilon);
                Debug.WriteLine($"Reduced from {inputPointBuffer.Count} points to {processedPointBuffer.Count} points.");

                foreach (var point in processedPointBuffer.Skip(1))
                {
                    outputPointBuffer.Enqueue(point);
                }

                inputPointBuffer.Clear();
            }

            // Output
            try
            {
                TimeSpan outputTimeThreshold = DateTime.Now - referenceTime - FilterTime;

                StrokerPoint nextPoint = default;
                bool shouldOutput = false;

                while (outputPointBuffer.TryPeek(out StrokerPoint potentialNextPoint))
                {
                    if (potentialNextPoint.Time <= outputTimeThreshold)
                    {
                        nextPoint = outputPointBuffer.Dequeue();
                        shouldOutput = true;
                        //Debug.WriteLine($"POINT TIME 1: {nextPoint.Time} - Thread: {Thread.CurrentThread.ManagedThreadId}");
                    }
                    else
                        break;
                }

                if (shouldOutput)
                {
                    TimeSpan writeDuration = DateTime.Now - lastWriteTime - TimeSpan.FromMilliseconds(3);
                    lastWriteTime = DateTime.Now;

                    if (writeDuration.TotalSeconds < 2)
                    {
                        Output?.Invoke(this, new OutputEventArgs(Math.Clamp(nextPoint.Position, 0, 1), writeDuration));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to output value: " + ex.Message);
            }
        }
    }

    public class OutputEventArgs : EventArgs
    {
        public double Position { get; } // From 0 to 1.
        public TimeSpan Duration { get; } // Time it should take to move to this position. Optional.

        public OutputEventArgs(double position, TimeSpan duration)
        {
            this.Position = position;
            this.Duration = duration;
        }
    }
}
