using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TeledongCommander.ViewModels;

namespace TeledongCommander
{
    /// <summary>
    /// Class that takes raw points as input via PutPositionAndProcessOutput, and does stuff like add delay, reduce number of points, etc, before outputting via event Output.
    /// Not all output devices need this. If PeakMotionMode is false and SkipFiltering is true, points are simply passed to the output event immediately upon input.
    /// </summary>
    public class OutputProcessor
    {
        public bool SkipFiltering { get; set; } = false;
        public double FilterStrength { get; set; } = 0.00;
        public TimeSpan FilterTime { get; set; } = TimeSpan.FromMilliseconds(0);
        public bool PeakMotionMode { get; set; } = true;

        readonly List<StrokerPoint> inputPointBuffer = new List<StrokerPoint>();
        readonly Queue<StrokerPoint> outputPointBuffer = new Queue<StrokerPoint>();
        readonly DateTime referenceTime = DateTime.Now;
        DateTime lastWriteTime = DateTime.Now;
        DateTime lastWriteTimePeak = DateTime.Now;
        double previousPosition = 0.9;
        StrokeDirection currentDirection = StrokeDirection.None;

        public event EventHandler<OutputEventArgs>? Output;

        /// <summary>
        /// This will send the current position (from 0 to 1) to processing, and trigger the Output event if a new position is ready to be sent to the device.
        /// Should be called as often and soon as possible, such as every time you have a new reading from the sensor.
        /// </summary>
        /// <param name="position"></param>
        public void PutPositionAndProcessOutput(double position)
        {
            var now = DateTime.Now - referenceTime;

            if (!PeakMotionMode)
            {
                if (SkipFiltering || FilterTime == TimeSpan.Zero || FilterStrength == 0)
                {
                    // Raw unfiltered stream of points. 
                    outputPointBuffer.Enqueue(new StrokerPoint(position, now));
                }
                else
                {
                    // Filter and queue chunks of input
                    inputPointBuffer.Add(new StrokerPoint(position, now));

                    if ((now - inputPointBuffer.First().Time) > FilterTime)
                    {
                        List<StrokerPoint> processedPointBuffer = RamerDouglasPeuckerNetV2.RamerDouglasPeucker.Reduce(inputPointBuffer.ToList(), FilterStrength);
                        Debug.WriteLine($"Reduced from {inputPointBuffer.Count} points to {processedPointBuffer.Count} points.");

                        foreach (var point in processedPointBuffer.Skip(1))
                        {
                            outputPointBuffer.Enqueue(point);
                        }

                        inputPointBuffer.Clear();
                    }
                }
            }
            else
            {
                // Peak motion write mode. Only writes a position after the motion on the Teledong has stopped/reversed.
                // Should mean that positions are usually only written twice per stroke, at the max/min amplitude peaks. 
                // This mode should be used if the device API favors more rare updates due to latency/bandwidth/capacity.

                bool shouldSendPosition = false;
                var positionDelta = position - previousPosition;

                if (currentDirection == StrokeDirection.Up)
                {
                    if (position <= previousPosition || DateTime.Now - lastWriteTimePeak > TimeSpan.FromSeconds(0.8))
                    {
                        Debug.WriteLine("Strokedir.: Up");

                        shouldSendPosition = true;

                        if (position <= previousPosition)
                            currentDirection = StrokeDirection.None;
                    }
                }
                else if (currentDirection == StrokeDirection.Down)
                {
                    if (position >= previousPosition || DateTime.Now - lastWriteTimePeak > TimeSpan.FromSeconds(0.8))
                    {
                        Debug.WriteLine("Strokedir.: Down");

                        shouldSendPosition = true;

                        if (position >= previousPosition)
                            currentDirection = StrokeDirection.None;
                    }
                }

                if (shouldSendPosition)
                {
                    if (DateTime.Now - lastWriteTimePeak < TimeSpan.FromMilliseconds(100) && positionDelta < 0.1)
                    {
                        lastWriteTimePeak = DateTime.Now;
                        shouldSendPosition = false; // Debouncing
                    }
                }

                if (Math.Abs(positionDelta) > 0.05)
                {
                    currentDirection = positionDelta > 0 ? StrokeDirection.Up : StrokeDirection.Down;
                    previousPosition = position;
                }

                if (currentDirection != StrokeDirection.None)
                    previousPosition = position;

                if (shouldSendPosition)
                {
                    lastWriteTimePeak = DateTime.Now;
                    outputPointBuffer.Enqueue(new StrokerPoint(previousPosition, now));
                }
            }


            // Process queue and output
            try
            {
                TimeSpan outputTimeThreshold = DateTime.Now - referenceTime - (SkipFiltering ? TimeSpan.Zero : FilterTime);

                StrokerPoint nextPoint = default;
                bool shouldOutput = false;

                while (outputPointBuffer.TryPeek(out StrokerPoint potentialNextPoint))
                {
                    if (potentialNextPoint.Time <= outputTimeThreshold)
                    {
                        nextPoint = outputPointBuffer.Dequeue();
                        shouldOutput = true;
                        //Debug.WriteLine($"POINT TIME: {nextPoint.Time}");
                    }
                    else
                        break;
                }

                if (shouldOutput)
                {
                    TimeSpan writeDuration = DateTime.Now - lastWriteTime - TimeSpan.FromMilliseconds(10);
                    if (writeDuration > TimeSpan.FromMilliseconds(1000))
                        writeDuration = TimeSpan.FromMilliseconds(500);
                    lastWriteTime = DateTime.Now;

                    Output?.Invoke(this, new OutputEventArgs(Math.Clamp(nextPoint.Position, 0, 1), writeDuration));
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
        /// <summary>
        /// From 0 to 1.
        /// </summary>
        public double Position { get; }
        /// <summary>
        /// Time it should take to move to this position. Optional.
        /// </summary>
        public TimeSpan Duration { get; }

        public OutputEventArgs(double position, TimeSpan duration)
        {
            this.Position = position;
            this.Duration = duration;
        }
    }


    public enum StrokeDirection
    {
        None,
        Up,
        Down,
    }
}
