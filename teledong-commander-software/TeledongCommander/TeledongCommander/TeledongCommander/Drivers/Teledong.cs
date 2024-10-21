//using KalmanFilters; // Unused as of now
using LibUsbDotNet;
using LibUsbDotNet.Main;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Teledong;

/// <summary>
/// Class to connect to and read position data from a Teledong device.
/// Depends on Libusb. You need the LibUsbDotNet NuGet package as well as libusb binaries (such as libusb-1.0.dll for Windows which can be downloaded from https://libusb.info/). 
/// SEE THE DOCUMENT IntergrationGuide.md for a guide on using this library.
/// </summary>
public class Teledong
{
    /// <summary>
    /// Get the current status of any connected Teledong.
    /// </summary>
    public TeledongState State { get; private set; } = TeledongState.NotConnected;

    /// <summary>
    /// Is true if the current Teledong appears to be improperly calibrated (receiving values far outside the calibration bounds). Should prompt the user to start a new calibration.
    /// </summary>
    public bool BadCalibrationWarning { get; private set; } = false;

    /// <summary>
    /// If true, then if the Teledong thinks that the user completely let go of the device mid-stroke, it will report the most recent position instead of the default positon of 1. 
    /// Can avoid sudden jumps.
    /// </summary>
    public bool KeepPositionAtRelease { get; set; } = false;

    /// <summary>
    /// In sunlight mode, a sensor is considered obscured if it receives light rather than does not receive light. 
    /// Should not be set manually, instead start a calibration which automatically chooses the best setting.
    /// </summary>
    public bool IsSunlightMode => sunlightMode;

    const ushort TELEDONG_PID = 0x8DF4;
    const ushort TELEDONG_VID = 0x10C4;

    List<byte> calibrationLowValues = new List<byte>();
    List<byte> calibrationHighValues = new List<byte>();

    //double[] lastPositions = new double[2] { 0, 0 };
    //DateTime[] timeOfLastPositions = new DateTime[2] { DateTime.UtcNow, DateTime.UtcNow - TimeSpan.FromMilliseconds(100) };
    //KalmanFilter1D? filter; // Unused as of now

    UsbDevice? device;
    UsbEndpointReader? endpointReader;
    UsbEndpointWriter? endpointWriter;
    Mutex usbMutex = new Mutex();
    int firmwareVersion = 0;
    bool sunlightMode = false;
    int badCalibrationCounter = 0;
    const int badCalibrationThreshold = 200;
    const int numPreviousPositions = 4;
    double[] previousPositions = new double[numPreviousPositions] { 0, 0, 0, 0 };

    /// <summary>
    /// Scans for and connects to the Teledong over USB. Must be called before any other method.
    /// </summary>
    /// <returns>True if Teledong was found, otherwise false.</returns>
    /// <exception cref="DllNotFoundException"></exception>
    public bool Connect()
    {
        if (calibrationHighValues.Count == 0)
        {
            // Fill with default calibration, but user should load or perform proper calibration later.
            while (calibrationLowValues.Count < 30)
            {
                calibrationHighValues.Add(255);
                calibrationLowValues.Add(0);
            }
        }

        // Go through connected USB devices, check if PID matches, connect and configure.
        UsbDevice.ForceLibUsbWinBack = true;
        try
        {
            foreach (UsbRegistry usbRegistry in UsbDevice.AllDevices)
            {
                if (usbRegistry.Pid == TELEDONG_PID && usbRegistry.Vid == TELEDONG_VID)
                {
                    if (usbRegistry.Open(out UsbDevice device))
                    {
                        IUsbDevice? libUsbDevice = device as IUsbDevice;
                        if (libUsbDevice != null)
                        {
                            libUsbDevice.SetConfiguration(1);
                            libUsbDevice.ClaimInterface(0);
                        }

                        byte[] buffer = new byte[128];
                        var setupPacket = new UsbSetupPacket((byte)UsbRequestType.TypeVendor, 2, 0x0002, 0, 0);
                        if (device.ControlTransfer(ref setupPacket, buffer, 0, out int lengthTransferred) == false)
                            return false;

                        endpointReader = device.OpenEndpointReader(ReadEndpointID.Ep01, 128, EndpointType.Bulk);
                        endpointWriter = device.OpenEndpointWriter(WriteEndpointID.Ep01, EndpointType.Bulk);
                        this.device = device;

                        State = TeledongState.Ok;
                        BadCalibrationWarning = true;

                        //filter = new KalmanFilter1D(initialMean: 0, initialStdDev: 0.5, measurement_sigma: 0.2, motion_sigma: 0.1);

                        return true;
                    }
                }
            }
        }
        catch (DllNotFoundException ex)
        {
            throw new DllNotFoundException("Could not find the libusb-1.0 binaries. You need to manually include the file when distributing your program, depending on the platform.\n" +
                "Libusb can be downloaded here: https://sourceforge.net/projects/libusb/files/libusb-1.0/libusb-1.0.26/\n" +
                "For example, for Windows x64, add the file libusb-1.0.dll to your build folder, found in the folder VS2015-x64\\dll in the archive libusb-1.0.26-binaries.7z downloaded from the link above.", ex);
        }
        return false;
    }

    /// <summary>
    /// Gets the current position (how deep the device is inserted into something), normalized based on the current calibration.
    /// Should be called at a regular interval. It is recommended to use a timer interval around ~50ms, in order for some optional features such as KeepPositionAtRelease to work properly.
    /// </summary>
    /// <returns>Position, from 1.0 = Nothing on the dildo, to 0.0 = Dildo fully inserted.</returns>
    /// <exception cref="Exception"></exception>
    public double GetPosition()
    {
        if (device == null)
            throw new Exception("Device is not connected.");

        // Todo: could make this more advanced. Possible to use machine learning to predict position even without needing calibration?

        var beforeReadTime = DateTime.UtcNow;

        // First pass, rough estimation based on sensor readings only
        var sensorValues = GetRawSensorValues().ToList();
        if (sensorValues.Count == 0)
        {
            State = TeledongState.Error;
            throw new Exception("Unexpected result from sensor readings: 0 sensor values returned");
        }

        var afterReadTime = DateTime.UtcNow;

        double totalValue = 0;
        int lastDetectionIndex = 0;

        // Find linear position by finding the first obscured sensor and then adding the fraction of the signal from the next sensor
        // This can probably be improved to make output even more linear and accurate.
        for (int i = 0; i < sensorValues.Count; i++)
        {
            var value = sensorValues[sensorValues.Count - 1 - i]; // Reversed order for easier calculation
            if (sunlightMode)
                value = 1 - value;

            if (value > 0.5f)
            {
                totalValue = i; // Treat all sensors below first confident detection as obscured
                lastDetectionIndex = i;
            }
            else if (i - lastDetectionIndex >= 2)
                value = 0;

            totalValue += Math.Clamp(value, 0, 1);
        }
        var firstEstimate = totalValue / sensorValues.Count;

        /*raw = firstEstimate;

        // Second pass, Kalman filter. 
        //STATE OF DEVELOPMENT: working-ish but not very useful. Either lagging, overshooting, or practically identical to the first estimate.
        var motion = Math.Clamp((lastPositions[0] - lastPositions[1]) / (timeOfLastPositions[0] - timeOfLastPositions[1]).TotalSeconds, -2, 2);
        filter.Update(firstEstimate);
        filter.Predict(motion * 0.01);

        var finalEstimate = Math.Clamp(filter.State, 0 , 1);

        lastPositions[1] = lastPositions[0];
        timeOfLastPositions[1] = timeOfLastPositions[0];
        lastPositions[0] = finalEstimate;
        timeOfLastPositions[0] = beforeReadTime.Add((afterReadTime - beforeReadTime) / 2); // Time between start and end of USB transaction*/

        //Debug.WriteLine($"first estimate: {firstEstimate.ToString("N2")},\t\tmotion: {motion.ToString("N2")},\t\ttime diff: {(timeOfLastPositions[0] - timeOfLastPositions[1]).TotalMilliseconds.ToString("N0")},\t\t pos diff: {(lastPositions[0] - lastPositions[1]).ToString("N2")},\t\t result: {finalEstimate.ToString("N2")},\t\t uncertainty: {filter.BeliefDistribution.StdDev.ToString("N2")}");

        var position = 1.0 - firstEstimate; // Inverting position to make compatible with other conventions like Buttplug.io

        if (KeepPositionAtRelease)
        {
            var estimatedCurrentPosition = previousPositions[1] + (previousPositions[0] - previousPositions[2]);

            if (position > 0.95 && estimatedCurrentPosition < 0.9)
            {
                position = (previousPositions[1] + previousPositions[2]) / 2;
            }
            else
            {
                for (int i = numPreviousPositions-1; i > 0; i--)
                {
                    previousPositions[i] = previousPositions[i - 1];
                }
                previousPositions[0] = position;

                position = (previousPositions[0] + previousPositions[1]) / 2;
            }
        }

        return position;
    }

    /// <summary>
    /// If in sunlight mode, a sensor is considered obscured if it receives light rather than does not receive light. 
    /// This mode can be used in bright environments, when the optical sensors can not detect reflections well because they get saturated by the ambient light.
    /// NB: Typically you do NOT want to set this manually, insted run a calibration routine which automatically detects whether or not to use sunlight mode.
    /// </summary>
    /// <param name="enableSunlightMode">Sunlight mode</param>
    /// <param name="useMutex">Whether to block other Teledong USB transfers while the sensor readout happens. Default true.</param>
    /// /// <exception cref="Exception"></exception>
    public void SetSunlightMode(bool enableSunlightMode, bool useMutex=true)
    {
        SendNonQueryCommand(TeledongCommands.SetSunlightMode, new byte[] { (byte)(enableSunlightMode ? 1 : 0) }, useMutex);
        sunlightMode = enableSunlightMode;
        Debug.WriteLine("Set sunlight mode: " + enableSunlightMode.ToString());
    }

    /// <summary>
    /// Gets all raw sensor values from the device.
    /// Meant for advanced tasks like calibration/debugging. Normally you would simply use GetPosition() instead.
    /// </summary>
    /// <param name="normalizeToCalibration">Whether to normalize the values to between 1.0 and 0.0 based on calibration instead of raw 0-255 values. Default true.</param>
    /// <param name="useMutex">Whether to block other Teledong USB transfers while the sensor readout happens. Default true.</param>
    /// <returns>Enumerator of available sensor values.</returns>
    /// <exception cref="Exception"></exception>
    public IEnumerable<double> GetRawSensorValues(bool normalizeToCalibration = true, bool useMutex = true)
    {
        if (device == null)
            throw new Exception("Device is not connected.");

        if (useMutex)
        {
            if (!usbMutex.WaitOne(TimeSpan.FromMilliseconds(100)))
            {
                State = TeledongState.Error;
                throw new Exception("USB Device is busy.");
            }
        }

        try
        {
            // Send command to read sensor values from MCU
            var errorCode2 = endpointWriter!.Write(new byte[] { (byte)'T', (byte)'C', (byte)TeledongCommands.GetSensorValues }, 300, out int writeTransferLength);
            if (errorCode2 == ErrorCode.Ok && writeTransferLength == 3)
            {
                // Read response
                byte[] readData = new byte[64];
                var errorCode = endpointReader!.Read(readData, 500, out int readTransferLength);
                if (errorCode == ErrorCode.Ok && readTransferLength >= 10)
                {
                    if (State != TeledongState.Calibrating)
                        State = TeledongState.Ok;

                    bool calibrationOkFlag = true;

                    int i = 0;
                    foreach (var value in ParseSensorValuePacket(readData, readTransferLength))
                    {
                        if (normalizeToCalibration)
                        {
                            var calibratedValue = (value - calibrationLowValues[i]) / (float)(calibrationHighValues[i] - calibrationLowValues[i]);

                            if (calibratedValue < -0.3 || calibratedValue > 1.3)
                                calibrationOkFlag = false;

                            yield return (double)Math.Clamp(calibratedValue, 0.0, 1.0);
                        }
                        else
                        {
                            yield return value;
                        }
                        i++;
                    }

                    if (!calibrationOkFlag)
                    {
                        badCalibrationCounter = Math.Min(++badCalibrationCounter, int.MaxValue);
                        if (badCalibrationCounter > badCalibrationThreshold)
                            BadCalibrationWarning = true;
                    }
                    else
                        badCalibrationCounter = Math.Max(--badCalibrationCounter, 0);
                }
                else
                {
                    Debug.WriteLine("Could not read USB packet. Error code: " + errorCode);
                    State = TeledongState.Error;
                }
            }
            else
            {
                Debug.WriteLine("Failed to send USB packet. Error code: " + errorCode2);
                State = TeledongState.Error;
            }
        }
        finally 
        {
            if (useMutex)
            {
                usbMutex.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// Gets the firmware version of the connected device.
    /// </summary>
    /// <returns>Firmware version ID.</returns>
    /// <exception cref="Exception"></exception>
    public int GetFirmwareVersion()
    {
        if (device == null)
            throw new Exception("Device is not connected.");

        if (firmwareVersion != 0)
            return firmwareVersion;

        if (!usbMutex.WaitOne(TimeSpan.FromMilliseconds(100)))
            throw new Exception("USB Device is busy.");

        try
        {
            // Send command to read sensor values from MCU
            var errorCode2 = endpointWriter!.Write(new byte[] { (byte)'T', (byte)'C', (byte)TeledongCommands.GetFirmwareVersion }, 500, out int writeTransferLength);
            if (errorCode2 == ErrorCode.Ok && writeTransferLength == 3)
            {
                // Read response
                byte[] readData = new byte[64];
                var errorCode = endpointReader!.Read(readData, 500, out int readTransferLength);
                if (errorCode == ErrorCode.Ok && readTransferLength >= 4)
                {
                    if ((char)readData[0] == 'T' && (char)readData[1] == 'R' && readData[2] == (byte)TeledongCommands.GetFirmwareVersion)
                    {
                        firmwareVersion = readData[3];
                        return firmwareVersion;
                    }
                }
                else
                    Debug.WriteLine("Could not read USB packet. Error code: " + errorCode);
            }
            else
                Debug.WriteLine("Failed to send USB packet. Error code: " + errorCode2);

            throw new Exception("Failed to get firmware version.");
        }
        finally
        {
            usbMutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Parses a USB packet response from the mcu with sensor values (header TR1), and returns a list of all the values, from 0-255
    /// </summary>
    /// <param name="readData">USB TR1 response packet content</param>
    /// <param name="transferLength">Length of USB received packet, as reported by Libusb</param>
    /// <returns>Sensor value enumerator. Returns empty enumerator on failure to parse.</returns>
    static IEnumerable<byte> ParseSensorValuePacket(byte[] readData, int transferLength)
    {
        if (transferLength > 3 || ((char)readData[0] == 'T' && (char)readData[1] == 'R' && (char)readData[2] == (byte)TeledongCommands.GetSensorValues))
        {
            int numSensors = readData[3];
            for (int i = 0; i < numSensors; i++)
            {
                if (4 + i >= transferLength)
                    break;

                yield return (byte)(readData[4 + i] - 1);
            }
            // OLD CODE FOR PROTOTYPE FIRMWARE:
            /*int i = 3;
            while (readData[i] != 0)
            {
                yield return readData[i + 1];
                i += 2;
            }*/
        }
    }

    /// <summary>
    /// Sends a raw command to the device, discarding the response. 
    /// Used internally, not normally needed for end users except for advanced use cases such as firmware updating.
    /// </summary>
    /// <param name="command">Command ID</param>
    /// <param name="extraData">Command parameter content. Only required for certain commands.</param>
    /// <param name="useMutex">Whether to block other Teledong USB transfers while transaction happens. Default true.</param>
    /// <exception cref="Exception"></exception>
    public void SendNonQueryCommand(TeledongCommands command, byte[]? extraData = null, bool useMutex = true)
    {
        if (device == null)
            throw new Exception("Device is not connected.");

        if (useMutex)
        {
            if (!usbMutex.WaitOne(TimeSpan.FromMilliseconds(100)))
                throw new Exception("USB Device is busy.");
        }

        try
        {
            var payload = new byte[] { (byte)'T', (byte)'C', (byte)command };
            if (extraData != null)
                payload = payload.Concat(extraData).ToArray();
            var errorCode2 = endpointWriter!.Write(payload, 500, out int writeTransferLength);
            if (errorCode2 == ErrorCode.Ok && writeTransferLength == payload.Length)
            {
                // Read response
                byte[] readData = new byte[64];
                var errorCode = endpointReader!.Read(readData, 500, out int readTransferLength);
                if (errorCode == ErrorCode.Ok && readTransferLength >= 3)
                {
                    if ((char)readData[0] == 'T' && (char)readData[1] == 'R' && readData[2] == (byte)command)
                    {
                        return;
                    }
                    else
                        throw new Exception("Unexpected response to USB command: " + errorCode);
                }
                else
                    throw new Exception("Failure when reading response to USB command: " + errorCode);
            }
            else
                throw new Exception("Failure when sending USB command: " + errorCode2);
        }
        finally
        {
            if (useMutex)
            {
                usbMutex.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// Retreives stored calibration values from the connected Teledong. You should once either call this, or start a new calibration with CalibrateAsync(), before starting to read position data.
    /// </summary>
    /// <exception cref="Exception"></exception>
    public void LoadCalibration()
    {
        if (device == null)
            throw new Exception("Device is not connected.");

        if (!usbMutex.WaitOne(TimeSpan.FromMilliseconds(300)))
            throw new Exception("USB Device is busy.");

        calibrationLowValues.Clear();
        calibrationHighValues.Clear();

        bool newDaylightMode = IsSunlightMode;

        try
        {
            // Send command to read sensor values from MCU
            var errorCode2 = endpointWriter!.Write(new byte[] { (byte)'T', (byte)'C', (byte)TeledongCommands.LoadCalibrationValues }, 500, out int writeTransferLength);
            if (errorCode2 == ErrorCode.Ok && writeTransferLength == 3)
            {
                // Read response
                byte[] readData = new byte[64];
                var errorCode = endpointReader!.Read(readData, 500, out int readTransferLength);
                if (errorCode == ErrorCode.Ok && readTransferLength >= 4)
                {
                    if ((char)readData[0] == 'T' && (char)readData[1] == 'R' && readData[2] == (byte)TeledongCommands.LoadCalibrationValues)
                    {
                        newDaylightMode = readData[4] == 1;

                        for (int i = 0; i < readData[3]; i++)
                        {
                            calibrationLowValues.Add(readData[5 + i*2]);
                            calibrationHighValues.Add(readData[6 + i*2]);
                        }
                    }
                }
                else
                    Debug.WriteLine("Could not read USB packet. Error code: " + errorCode); // todo throw instead of print
            }
            else
                Debug.WriteLine("Failed to send USB packet. Error code: " + errorCode2);
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to load calibration values", ex);
        }
        finally
        {
            while (calibrationLowValues.Count < 30) // Pad the calibration to support up to 30 sensors, for backwards/future-compatibility
            {
                calibrationLowValues.Add(0);
                calibrationHighValues.Add(255);
            }

            usbMutex.ReleaseMutex();
        }

        BadCalibrationWarning = false;
        SetSunlightMode(newDaylightMode);
    }


    /// <summary>
    /// Starts a calibration routine. 
    /// For the duration of the calibration, the user should stroke the device up and down, alternating between obscuring all sensors, and not obscuring any sensors.
    /// The high and low values for each sensor will be recorded, and used as calibration normalization bounds.
    /// </summary>
    /// <param name="shouldSave">If true, the resulting calibration will be stored in the device, and can be loaded with LoadCalibration() later. Default true.</param>
    /// <param name="duration">Duration of the calibration routine. Default 10 seconds.</param>
    /// <returns>Async task handle</returns>
    /// <exception cref="Exception"></exception>
    public Task CalibrateAsync(bool shouldSave = true, TimeSpan? duration = null)
    {
        if (device == null)
            throw new Exception("Device is not connected.");
        if (duration == null)
            duration = TimeSpan.FromSeconds(10);


        return Task.Run(() =>
        {
            if (!usbMutex.WaitOne(TimeSpan.FromMilliseconds(1000)))
                throw new Exception("USB Device is busy.");

            State = TeledongState.Calibrating;

            try
            {
                // Todo: maybe implement some smoothing/filtering in case of occasional anomalous readings
                DateTime startTime = DateTime.UtcNow;
                int numSensors = 0;

                var lowValuesIndoor = new List<byte>();
                var highValuesIndoor = new List<byte>();
                var lowValuesSunlight = new List<byte>();
                var highValuesSunlight = new List<byte>();

                foreach (var sensorValue in GetRawSensorValues(normalizeToCalibration: false, useMutex: false)) // Check how many sensors there are
                {
                    lowValuesIndoor.Add(byte.MaxValue);
                    highValuesIndoor.Add(byte.MinValue);
                    lowValuesSunlight.Add(byte.MaxValue);
                    highValuesSunlight.Add(byte.MinValue);
                    numSensors++;
                }

                Thread.Sleep(20);

                // Test indoor mode conditions

                SendNonQueryCommand(TeledongCommands.SetSunlightMode, new byte[] {0}, useMutex: false);

                Thread.Sleep(20);

                while (DateTime.UtcNow - startTime < duration/2)
                {
                    int i = 0;
                    foreach (var sensorValue in GetRawSensorValues(normalizeToCalibration: false, useMutex: false))
                    {
                        if (sensorValue < lowValuesIndoor[i])
                            lowValuesIndoor[i] = (byte)sensorValue;
                        if (sensorValue > highValuesIndoor[i])
                            highValuesIndoor[i] = (byte)sensorValue;

                        i++;
                    }

#if DEBUG
                    Debug.WriteLine("\nNew indoor lows:");
                    for (i = 0; i < numSensors; i++)
                    {
                        Debug.Write(lowValuesIndoor[i] + "\t\t");
                    }
                    Debug.WriteLine("\nNew indoor highs:");
                    for (i = 0; i < numSensors; i++)
                    {
                        Debug.Write(highValuesIndoor[i] + "\t\t");
                    }
#endif

                    Thread.Sleep(20);
                }

                // Test sunlight mode conditions

                SendNonQueryCommand(TeledongCommands.SetSunlightMode, new byte[] { 1 }, useMutex: false);

                Thread.Sleep(20);

                while (DateTime.UtcNow - startTime < duration)
                {
                    int i = 0;
                    foreach (var sensorValue in GetRawSensorValues(normalizeToCalibration: false, useMutex: false))
                    {
                        if (sensorValue < lowValuesSunlight[i])
                            lowValuesSunlight[i] = (byte)sensorValue;
                        if (sensorValue > highValuesSunlight[i])
                            highValuesSunlight[i] = (byte)sensorValue;

                        i++;
                    }

#if DEBUG
                    Debug.WriteLine("\nNew sunlight lows:");
                    for (i = 0; i < numSensors; i++)
                    {
                        Debug.Write(lowValuesSunlight[i] + "\t\t");
                    }
                    Debug.WriteLine("\nNew sunlight highs:");
                    for (i = 0; i < numSensors; i++)
                    {
                        Debug.Write(highValuesSunlight[i] + "\t\t");
                    }
#endif

                    Thread.Sleep(20);
                }


                // Apply calibration

                calibrationLowValues.Clear();
                calibrationHighValues.Clear();

                int sumSignalStrengthIndoors = 0;
                int sumSignalStrengthSunlight = 0;
                for (int i = 0; i < numSensors; i++)
                {
                    sumSignalStrengthIndoors += highValuesIndoor[i] - lowValuesIndoor[i];
                    sumSignalStrengthSunlight += highValuesSunlight[i] - lowValuesSunlight[i];
                }

                Debug.WriteLine($"\nSunlight diff.: {sumSignalStrengthSunlight}, indoors diff.: {sumSignalStrengthIndoors}");
                if (sumSignalStrengthSunlight > sumSignalStrengthIndoors * 1.3)
                {
                    SetSunlightMode(true, useMutex: false);
                    calibrationLowValues.AddRange(lowValuesSunlight);
                    calibrationHighValues.AddRange(highValuesSunlight);
                }
                else
                {
                    SetSunlightMode(false, useMutex: false);
                    calibrationLowValues.AddRange(lowValuesIndoor);
                    calibrationHighValues.AddRange(highValuesIndoor);
                }

                while (calibrationLowValues.Count < 30) // Pad the calibration to support up to 30 sensors, for backwards/future-compatibility
                {
                    calibrationLowValues.Add(0);
                    calibrationHighValues.Add(255);
                }

                if (shouldSave)
                {
                    Thread.Sleep(20);

                    List<byte> commandPayload = new() { (byte)numSensors, (byte)(IsSunlightMode ? 1 : 0) };
                    for (int i = 0; i < numSensors; i++)
                    {
                        commandPayload.Add(calibrationLowValues[i]);
                        commandPayload.Add(calibrationHighValues[i]);
                    }

                    SendNonQueryCommand(TeledongCommands.SaveCalibrationValues, commandPayload.ToArray(), useMutex: false);
                }

                Thread.Sleep(20);

            }
            finally
            {
                try
                {
                    usbMutex.ReleaseMutex();
                    BadCalibrationWarning = false;
                    State = TeledongState.Ok;
                }
                catch { }
            }
        });
    }

    /// <summary>
    /// Disconnects from the Teledong and frees dangling threads/resources.
    /// </summary>
    public void Disconnect()
    {
        try
        {
            if (device != null)
            {
                byte[] buffer = new byte[128];
                var setupPacket = new UsbSetupPacket(0x41, 2, 0x0004, 0, 0);
                device.ControlTransfer(ref setupPacket, buffer, 0, out int _);
                device.Close();
            }
            UsbDevice.Exit();
            device = null;
            firmwareVersion = 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Failed to gracefully shut down Teledong USB: " + ex.Message);
        }
    }

    ~Teledong()
    {
        Disconnect();
    }

}

public enum TeledongState
{
    NotConnected,
    Ok,
    Calibrating,
    Error
}

public enum TeledongCommands : byte
{
    GetSensorValues = 0x01,
    GetFirmwareVersion = 0x02,
    SaveCalibrationValues = 0x03,
    LoadCalibrationValues = 0x04,
    SaveUserData = 0x05,
    ReadUserData = 0x06,
    SetSunlightMode = 0x07,
    GetSunlightMode = 0x08,
    EnterBootloader = 0xFE,
}
