﻿//using KalmanFilters; // Unused as of now
using LibUsbDotNet;
using LibUsbDotNet.Main;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Teledong
{
	/// <summary>
	/// Class to connect to and read position data from a Teledong device.
	/// Depends on Libusb. You need the LibUsbDotNet NuGet package as well as libusb binaries (such as libusb-1.0.dll for Windows which can be downloaded from https://libusb.info/). 
	/// See Teledong Commander software source for example usage.
	/// </summary>
    public class Teledong
    {
        /// <summary>
        /// Get whether a Teledong is currently connected and ready to use.
        /// </summary>
        public bool IsConnected => device != null && device.IsOpen;
        /// <summary>
        /// If true, a sensor is considered obscured if it receives light rather than does not receive light. 
        /// This mode can be used in bright environments like daylight, when the sensors can not detect reflections well because they get saturated by the ambient light.
        /// </summary>
        public bool InverseSensorMode { get; set; } = false;

        const ushort TELEDONG_PID = 0x8DF4;
        const ushort TELEDONG_VID = 0x10C4;

        List<byte> calibrationLowValues = new List<byte>();
        List<byte> calibrationHighValues = new List<byte>();

        double[] lastPositions = new double[2] { 0, 0 };
        DateTime[] timeOfLastPositions = new DateTime[2] { DateTime.Now, DateTime.Now - TimeSpan.FromMilliseconds(100) };
        //KalmanFilter1D? filter; // Unused as of now

        UsbDevice? device;
        UsbEndpointReader? endpointReader;
        UsbEndpointWriter? endpointWriter;
        Mutex usbMutex = new Mutex();
        int firmwareVersion = 0;

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

            // Go through connectd USB devices, check if PID matches, connect and configure.
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

                            //var setupPacket = new UsbSetupPacket(0x41, 0, 0xFFFF, 0, 0);
                            byte[] buffer = new byte[128];
                            //if (device.ControlTransfer(ref setupPacket, buffer, 0, out int lengthTransferred) == false)
                            //    return false;

                            var setupPacket = new UsbSetupPacket(0x41, 2, 0x0002, 0, 0);
                            if (device.ControlTransfer(ref setupPacket, buffer, 0, out int lengthTransferred) == false)
                                return false;

                            endpointReader = device.OpenEndpointReader(ReadEndpointID.Ep01, 128, EndpointType.Bulk);
                            endpointWriter = device.OpenEndpointWriter(WriteEndpointID.Ep01, EndpointType.Bulk);
                            this.device = device;

                            //filter = new KalmanFilter1D(initialMean: 0, initialStdDev: 0.5, measurement_sigma: 0.2, motion_sigma: 0.1);

                            return true;
                        }
                    }
                }
            }
            catch (DllNotFoundException ex)
            {
                throw new DllNotFoundException("Could not find the libusb-1.0 binaries. You need to manually include the file when distributing your program, depending on the platform.\n" +
                    "Libusb can be downloaded here: https://sourceforge.net/projects/libusb/files/libusb-1.0/libusb-1.0.22/\n" +
                    "For example, for Windows x64, add the file libusb-1.0.dll to your build folder, found in the folder MS64\\dll in the archive libusb-1.0.22.7z downloaded from the link above.", ex);
            }
            return false;
        }

        /// <summary>
        /// Gets the current position of the sensor array, normalized based on the current calibration.
        /// </summary>
        /// <returns>Position, from 1.0 = Nothing on the dildo, to 0.0 = Dildo fully inserted.</returns>
        /// <exception cref="Exception"></exception>
        public double GetPosition()
        {
            if (device == null)
                throw new Exception("Device is not connected.");

            // Todo: could make this more advanced. Possible to use machine learning to predict position even without needing calibration?

            var beforeReadTime = DateTime.Now;

            // First pass, rough estimation based on sensor readings only
            var sensorValues = GetRawSensorValues().ToList();
            if (sensorValues.Count == 0)
                throw new Exception("Unexpected result from sensor readings: 0 sensor values returned");

            var afterReadTime = DateTime.Now;

            double totalValue = 0;
            int lastDetectionIndex = 0;

            // Find linear position by finding the first obscured sensor and then adding the fraction of the signal from the next sensor
            // This can probably be improved to make output even more linear and accurate.
            for (int i = 0; i < sensorValues.Count; i++)
            {
                var value = sensorValues[sensorValues.Count - 1 - i]; // Reversed order for easier calculation
                if (InverseSensorMode)
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

            return 1.0 - firstEstimate; // Inverting position to make compatible with other conventions like Buttplug.io
        }

        /// <summary>
        /// Gets all raw sensor values from the device.
        /// Meant for advanced tasks like calibration/debugging. Normally you would simply use GetPosition() instead.
        /// </summary>
        /// <param name="normalizeToCalibration">Whether to normalize the values to between 1.0 and 0.0 based on calibration, or return raw 0-255 values.</param>
        /// <param name="useMutex">Whether to block other Teledong USB transfers while transaction happens. Default true.</param>
        /// <returns>Enumerator of available sensor values.</returns>
        /// <exception cref="Exception"></exception>
        public IEnumerable<double> GetRawSensorValues(bool normalizeToCalibration = true, bool useMutex = true)
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
                // Send command to read sensor values from MCU
                var errorCode2 = endpointWriter!.Write(new byte[] { (byte)'T', (byte)'C', (byte)TeledongCommands.GetSensorValues }, 300, out int writeTransferLength);
                if (errorCode2 == ErrorCode.Ok && writeTransferLength == 3)
                {
                    // Read response
                    byte[] readData = new byte[64];
                    var errorCode = endpointReader!.Read(readData, 500, out int readTransferLength);
                    if (errorCode == ErrorCode.Ok && readTransferLength >= 10)
                    {
                        int i = 0;
                        foreach (var value in ParseSensorValuePacket(readData, readTransferLength))
                        {
                            yield return normalizeToCalibration ? (double)Math.Clamp((value - calibrationLowValues[i]) / (float)(calibrationHighValues[i] - calibrationLowValues[i]), 0.0, 1.0) : value;
                            i++;
                        }
                    }
                    else
                        Debug.WriteLine("Could not read USB packet. Error code: " + errorCode);
                }
                else
                    Debug.WriteLine("Failed to send USB packet. Error code: " + errorCode2);
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

            if (!usbMutex.WaitOne(TimeSpan.FromMilliseconds(100)))
                throw new Exception("USB Device is busy.");

            if (firmwareVersion != 0)
                return firmwareVersion;

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
        /// Sends a raw command to the device, when the response can be discarded. 
        /// Used internally, not normally needed for end users except for advanced use cases.
        /// </summary>
        /// <param name="command">Command ID</param>
        /// <param name="extraData">Command parameter content</param>
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
                    if (errorCode == ErrorCode.Ok && readTransferLength >= 4)
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
        /// Retreives stored calibration values from the connected Teledong. You should either call this, or start a new calibration with CalibrateAsync(), before reading the position.
        /// </summary>
        /// <exception cref="Exception"></exception>
        public void LoadCalibration()
        {
            calibrationLowValues.Clear();
            calibrationHighValues.Clear();

            if (device == null)
                throw new Exception("Device is not connected.");

            if (!usbMutex.WaitOne(TimeSpan.FromMilliseconds(300)))
                throw new Exception("USB Device is busy.");

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
                            for (int i = 0; i < readData[3]; i++)
                            {
                                calibrationLowValues.Add(readData[4 + i*2]);
                                calibrationHighValues.Add(readData[5 + i*2]);
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

                try
                {
                    DateTime startTime = DateTime.Now;
                    int numSensors = 0;

                    calibrationLowValues.Clear();
                    calibrationHighValues.Clear();

                    foreach (var sensorValue in GetRawSensorValues(normalizeToCalibration: false, useMutex: false)) // Check how many sensors there are
                    {
                        calibrationLowValues.Add(byte.MaxValue);
                        calibrationHighValues.Add(byte.MinValue);
                        numSensors++;
                    }
                    while (calibrationLowValues.Count < 30) // Pad the calibration to support up to 30 sensors, for backwards/future-compatibility
                    {
                        calibrationLowValues.Add(0);
                        calibrationHighValues.Add(255);
                    }

                    while (DateTime.Now - startTime < duration)
                    {
                        int i = 0;
                        foreach (var sensorValue in GetRawSensorValues(normalizeToCalibration: false, useMutex: false))
                        {
                            if (sensorValue < calibrationLowValues[i])
                                calibrationLowValues[i] = (byte)sensorValue;
                            if (sensorValue > calibrationHighValues[i])
                                calibrationHighValues[i] = (byte)sensorValue;

                            i++;
                        }

                        // Todo, maybe implement some smoothing/filtering in case of occasional anomalous readings

#if DEBUG
                        Debug.WriteLine("\nNew lows:");
                        for (i = 0; i < numSensors; i++)
                        {
                            Debug.Write(calibrationLowValues[i] + "\t\t");
                        }
                        Debug.WriteLine("\nNew highs:");
                        for (i = 0; i < numSensors; i++)
                        {
                            Debug.Write(calibrationHighValues[i] + "\t\t");
                        }
#endif

                        Thread.Sleep(20);
                    }

                    if (shouldSave)
                    {
                        List<byte> commandPayload = new() { (byte)numSensors };
                        for (int i = 0; i < numSensors; i++)
                        {
                            commandPayload.Add(calibrationLowValues[i]);
                            commandPayload.Add(calibrationHighValues[i]);
                        }

                        SendNonQueryCommand(TeledongCommands.SaveCalibrationValues, commandPayload.ToArray(), useMutex: false);
                    }

                }
                finally
                {
                    try
                    {
                        usbMutex.ReleaseMutex();
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

    public enum TeledongCommands : byte
    {
        GetSensorValues = 0x01,
        GetFirmwareVersion = 0x02,
        SaveCalibrationValues = 0x03,
        LoadCalibrationValues = 0x04,
        SaveUserData = 0x05,
        ReadUserData = 0x06,
        EnterBootloader = 0xFE,
    }
}
