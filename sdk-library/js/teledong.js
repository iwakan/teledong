/// Class to connect to and read position data from a Teledong device.
/// Needs WebUSB which is not supported in all browsers. For example Firefox doesn't work.
/// See example.html for example usage.
/// SEE THE DOCUMENT IntergrationGuide.md for a guide on using this library.

class Teledong {
	
    constructor() {
        this.State = TeledongState.NotConnected; /// Get the current status of any connected Teledong.
        this.BadCalibrationWarning = false; /// Is true if the current Teledong appears to be improperly calibrated (receiving values far outside the calibration bounds). Should prompt the user to start a new calibration.
        this.KeepPositionAtRelease = false; /// If true, then if the Teledong thinks that the user completely let go of the device mid-stroke, it will report the most recent position instead of the default positon of 1. Can avoid sudden jumps.

        this.TELEDONG_PID = 0x8DF4;
        this.TELEDONG_VID = 0x10C4;
        this.device = null;
        this.endpointIn = null;
        this.endpointOut = null;
        this.calibrationLowValues = Array(30).fill(0);
        this.calibrationHighValues = Array(30).fill(255);
        this.sunlightMode = false;
        this.previousPositions = Array(4).fill(0);
        this.badCalibrationCounter = 0;
        this.badCalibrationThreshold = 200;
		this.endpointIn = 1;
		this.endpointOut = 1;
    }

    /// Scans for and connects to the Teledong over USB. Must be called before any other method.
    /// Returns true if Teledong was found, otherwise false.
    async connect() {
        try {
            this.device = await navigator.usb.requestDevice({ filters: [{ vendorId: this.TELEDONG_VID, productId: this.TELEDONG_PID }] });

            if (!this.device) {
                console.error('Device not found');
                return false;
            }

            await this.device.open();
            await this.device.selectConfiguration(1);
            await this.device.claimInterface(0);

            this.State = TeledongState.Ok;
            this.BadCalibrationWarning = true;

			let buffer = new Uint8Array(0); 
			await this.device.controlTransferOut({
				requestType: 'vendor',
				recipient: 'device',
				request: 2,
				value: 0x0002,
				index: 0,
			}, buffer);
			
            console.log("Connected to Teledong");

            await this.loadCalibration();

            return true;
			
        } catch (error) {
            console.error('Error in connecting to device:', error);
            return false;
        }
    }

    /// Gets the current position of the sensor array, normalized based on the current calibration.
    /// Should be called at a regular interval. It is recommended to use a timer interval around ~50ms, in order for some optional features such as KeepPositionAtRelease to work properly.
    /// Return position, from 1.0 = Nothing on the dildo, to 0.0 = Dildo fully inserted.
    async getPosition() {
        if (!this.device) 
			throw new Error('Device is not connected.');

        let sensorValues = await this.getRawSensorValues();

        if (sensorValues.length === 0) {
            this.State = TeledongState.Error;
            throw new Error('Unexpected result: 0 sensor values returned.');
        }

        let totalValue = 0;
        let lastDetectionIndex = 0;
		let obscuredThreshold = 0.5;

        for (let i = 0; i < sensorValues.length; i++) {
            let value = sensorValues[sensorValues.length - 1 - i];
            if (this.sunlightMode) 
				value = 1 - value;
			
			// Smooth with neighbor sensors to mitigate sensor outlines
			if (i == 0)
			{
				// Top sensor, don't smooth
			}
			else if (i == 1)
			{
				// Second top sensor, smooth with two neighboring sensors
				let previousValue = sensorValues[sensorValues.length - 1 - i + 1]; // Reversed order for easier calculation
				let nextValue = sensorValues[sensorValues.length - 1 - i - 1]; // Reversed order for easier calculation
				if (this.sunlightMode)
				{
					previousValue = 1 - previousValue;
					nextValue = 1 - nextValue;
				}

				let numObscured = 0;
				let isObscured = (value > obscuredThreshold);
				if (isObscured)
					numObscured++;
				if (previousValue > obscuredThreshold)
					numObscured++;
				if (nextValue > obscuredThreshold)
					numObscured++;
				if ((isObscured && numObscured <= 1) || (!isObscured && numObscured >= 2))
					value = (value + previousValue + nextValue) / 3.0;

			}
			else if (i == sensorValues.length - 1)
			{
				// Bottom sensor, smooth with three above it
				let previousValue1 = sensorValues[sensorValues.length - 1 - i + 1]; // Reversed order for easier calculation
				let previousValue2 = sensorValues[sensorValues.length - 1 - i + 2]; // Reversed order for easier calculation
				let previousValue3 = sensorValues[sensorValues.length - 1 - i + 3]; // Reversed order for easier calculation
				if (this.sunlightMode)
				{
					previousValue1 = 1 - previousValue1;
					previousValue2 = 1 - previousValue2;
					previousValue3 = 1 - previousValue3;
				}

				let numObscured = 0;
				let isObscured = (value > obscuredThreshold);
				if (isObscured)
					numObscured++;
				if (previousValue1 > obscuredThreshold)
					numObscured++;
				if (previousValue2 > obscuredThreshold)
					numObscured++;
				if (previousValue3 > obscuredThreshold)
					numObscured++;
				if ((isObscured && numObscured <= 2) || (!isObscured && numObscured >= 3))
					value = (value + previousValue1 + previousValue2 + previousValue3) / 4.0;
			}
			else
			{
				// Middle sensor, smooth with three neighboring sensors
				let previousValue1 = sensorValues[sensorValues.length - 1 - i + 1]; // Reversed order for easier calculation
				let previousValue2 = sensorValues[sensorValues.length - 1 - i + 2]; // Reversed order for easier calculation
				let nextValue = sensorValues[sensorValues.length - 1 - i - 1]; // Reversed order for easier calculation
				if (this.sunlightMode)
				{
					previousValue1 = 1 - previousValue1;
					previousValue2 = 1 - previousValue2;
					nextValue = 1 - nextValue;
				}

				let numObscured = 0;
				let isObscured = (value > obscuredThreshold);
				if (isObscured)
					numObscured++;
				if (previousValue1 > obscuredThreshold)
					numObscured++;
				if (previousValue2 > obscuredThreshold)
					numObscured++;
				if (nextValue > obscuredThreshold)
					numObscured++;
				if ((isObscured && numObscured <= 2) || (!isObscured && numObscured >= 3))
					value = (value + previousValue1 + previousValue2 + nextValue) / 4.0;
			}

            if (value > obscuredThreshold) {
                totalValue = i;
                lastDetectionIndex = i;
            } else if (i - lastDetectionIndex >= 2) {
                value = 0;
            }

            totalValue += Math.min(Math.max(value, 0), 1);
        }
        let firstEstimate = totalValue / sensorValues.length;

        let position = 1.0 - firstEstimate; // Inverting position to make compatible with existing conventions like Buttplug.io

        if (this.KeepPositionAtRelease) {
            let estimatedCurrentPosition = this.previousPositions[1] + (this.previousPositions[0] - this.previousPositions[2]);

            if (position > 0.95 && estimatedCurrentPosition < 0.9) {
                position = (this.previousPositions[1] + this.previousPositions[2]) / 2;
            } else {
                for (let i = this.previousPositions.length - 1; i > 0; i--) {
                    this.previousPositions[i] = this.previousPositions[i - 1];
                }
                this.previousPositions[0] = position;
                position = (this.previousPositions[0] + this.previousPositions[1]) / 2;
            }
        }

        return position;
    }



    /// Retreives stored calibration values from the connected Teledong. Called automatically in connect().
    async loadCalibration() {
        if (!this.device) {
            throw new Error("Device is not connected.");
        }

        this.calibrationLowValues = [];
        this.calibrationHighValues = [];

        let newSunlightMode = this.sunlightMode;

        try {
            let data = await this.sendCommand(TeledongCommands.LoadCalibrationValues);

            if (data && data.length >= 4) {
                newSunlightMode = data[4] === 1;

                for (let i = 0; i < data[3]; i++) {
                    this.calibrationLowValues.push(data[5 + i * 2]);
                    this.calibrationHighValues.push(data[6 + i * 2]);
                }
            } else {
                console.error("Could not read USB packet.");
            }
        } catch (error) {
            throw new Error("Failed to load calibration values", error);
        } finally {
            while (this.calibrationLowValues.length < 30) { // Pad the calibration to support up to 30 sensors, for backwards/future-compatibility
                this.calibrationLowValues.push(0);
                this.calibrationHighValues.push(255);
            }
        }

        this.BadCalibrationWarning = false;
        await this.setSunlightMode(newSunlightMode);
    }

    /// Starts a calibration routine.
    /// For the duration of the calibration, the user should stroke the device up and down, alternating between obscuring all sensors, and not obscuring any sensors.
    /// The high and low values for each sensor will be recorded, and used as calibration normalization bounds.
    async calibrate(shouldSave = true, durationSeconds = 10) {
        if (this.device == null) {
            throw new Error("Device is not connected.");
        }

        this.State = TeledongState.Calibrating;

        console.log("Calibrating Teledong for number of seconds: " + durationSeconds);
		
		await new Promise(resolve => setTimeout(resolve, 30));

        let duration = durationSeconds * 1000; // Convert to milliseconds

        let startTime = Date.now();
        let numSensors = 0;

        let lowValuesIndoor = [];
        let highValuesIndoor = [];
        let lowValuesSunlight = [];
        let highValuesSunlight = [];

        let rawSensorValues = await this.getRawSensorValues(false);

        rawSensorValues.forEach(() => {
            lowValuesIndoor.push(255);
            highValuesIndoor.push(0);
            lowValuesSunlight.push(255);
            highValuesSunlight.push(0);
            numSensors++;
        });

        await new Promise(resolve => setTimeout(resolve, 20));

        // Test indoor mode conditions
        await this.sendCommand(TeledongCommands.SetSunlightMode, [0]);

        await new Promise(resolve => setTimeout(resolve, 20));

        while (Date.now() - startTime < duration / 2) {
            let i = 0;
            rawSensorValues = await this.getRawSensorValues(false);
            rawSensorValues.forEach(sensorValue => {
                if (sensorValue < lowValuesIndoor[i]) lowValuesIndoor[i] = sensorValue;
                if (sensorValue > highValuesIndoor[i]) highValuesIndoor[i] = sensorValue;
                i++;
            });

            await new Promise(resolve => setTimeout(resolve, 20));
        }

        // Test sunlight mode conditions
        await this.sendCommand(TeledongCommands.SetSunlightMode, [1]);

        await new Promise(resolve => setTimeout(resolve, 20));

        while (Date.now() - startTime < duration) {
            let i = 0;
            rawSensorValues = await this.getRawSensorValues(false);
            rawSensorValues.forEach(sensorValue => {
                if (sensorValue < lowValuesSunlight[i]) lowValuesSunlight[i] = sensorValue;
                if (sensorValue > highValuesSunlight[i]) highValuesSunlight[i] = sensorValue;
                i++;
            });

            await new Promise(resolve => setTimeout(resolve, 20));
        }

        // Apply calibration
        this.calibrationLowValues = [];
        this.calibrationHighValues = [];

        let sumSignalStrengthIndoors = 0;
        let sumSignalStrengthSunlight = 0;
        for (let i = 0; i < numSensors; i++) {
            sumSignalStrengthIndoors += highValuesIndoor[i] - lowValuesIndoor[i];
            sumSignalStrengthSunlight += highValuesSunlight[i] - lowValuesSunlight[i];
        }

        console.log(`Sunlight diff.: ${sumSignalStrengthSunlight}, indoors diff.: ${sumSignalStrengthIndoors}`);
        if (sumSignalStrengthSunlight > sumSignalStrengthIndoors * 1.3) {
            await this.setSunlightMode(true);
            this.calibrationLowValues = lowValuesSunlight.slice();
            this.calibrationHighValues = highValuesSunlight.slice();
        } else {
            await this.setSunlightMode(false);
            this.calibrationLowValues = lowValuesIndoor.slice();
            this.calibrationHighValues = highValuesIndoor.slice();
        }

        while (this.calibrationLowValues.length < 30) {
            this.calibrationLowValues.push(0);
            this.calibrationHighValues.push(255);
        }

        if (shouldSave) {
            await new Promise(resolve => setTimeout(resolve, 20));

            let commandPayload = [numSensors, this.sunlightMode ? 1 : 0];
            for (let i = 0; i < numSensors; i++) {
                commandPayload.push(this.calibrationLowValues[i]);
                commandPayload.push(this.calibrationHighValues[i]);
            }

            await this.sendCommand(TeledongCommands.SaveCalibrationValues, commandPayload);
        }

        await new Promise(resolve => setTimeout(resolve, 20));

        this.badCalibrationWarning = false;
        this.State = TeledongState.Ok;
    }

    /// Disconnects from the Teledong and frees dangling threads/resources.
    async disconnect() {
        try {
            if (this.device) {
                await this.device.close();
            }
            this.device = null;
            this.State = TeledongState.NotConnected;

            console.log("Disconnected from Teledong");

        } catch (error) {
            console.error('Failed to disconnect device:', error);
        }
    }

    /// Gets all raw sensor values from the device.
    /// Meant for advanced tasks like calibration/debugging. Normally you would simply use getPosition() instead.
    async getRawSensorValues(normalizeToCalibration = true) {
        if (!this.device) 
			throw new Error('Device is not connected.');

        try {
            await this.device.transferOut(this.endpointOut, new Uint8Array([0x54, 0x43, TeledongCommands.GetSensorValues]));
            let response = await this.device.transferIn(this.endpointIn, 64);
			let data = Array.from(new Uint8Array(response.data.buffer));

            if (this.State !== TeledongState.Calibrating) 
                this.State = TeledongState.Ok;

            let calibrationOkFlag = true;
            let sensorValues = this.parseSensorValuePacket(data);
			
			if (normalizeToCalibration) {
				let offset = 0;
				for (let i = 0; i < sensorValues.length; i++) {
					if ((this.calibrationHighValues[i + offset] <= this.calibrationLowValues[i + offset]))
					{
						// Invalid calibration, maybe faulty sensor, ignore this reading.
						sensorValues.splice(i,i);
						offset++;
						continue;
					}
					let calibratedValue = (sensorValues[i] - this.calibrationLowValues[i + offset]) / (this.calibrationHighValues[i + offset] - this.calibrationLowValues[i + offset]);

					if (calibratedValue < -0.3 || calibratedValue > 1.3) 
						calibrationOkFlag = false;

					sensorValues[i] = Math.min(Math.max(calibratedValue, 0.0), 1.0);
				}
			}
			return sensorValues;

        } catch (error) {
            this.State = TeledongState.Error;
            console.error('Error reading sensor values:', error);
            return [];
        }
    }

    /// If in sunlight mode, a sensor is considered obscured if it receives light rather than does not receive light.
    /// This mode can be used in bright environments, when the optical sensors can not detect reflections well because they get saturated by the ambient light.
    /// NB: Typically you do NOT want to set this manually, insted run a calibration routine "calibrate()" which automatically detects whether or not to use sunlight mode.
    async setSunlightMode(enabled) {
        await this.sendCommand(TeledongCommands.SetSunlightMode, [enabled ? 1 : 0])
        this.sunlightMode = enabled;
    }

    /// Sends a raw command to the device, returning the response.
    /// Used internally, not normally needed for end users except for advanced use cases such as firmware updating.
    async sendCommand(command, extraData = []) {
        if (!this.device) 
			throw new Error('Device is not connected.');
		
		let payload = new Uint8Array([0x54, 0x43, command, ...extraData]);
		await this.device.transferOut(this.endpointOut, payload);
		let result = await this.device.transferIn(this.endpointIn, 64);
		let response = new Uint8Array(result.data.buffer);

		if (response.length >= 3 && response[0] === 0x54 && response[1] === 0x52 && response[2] === command) {
			return response;
		} else {
			throw new Error('Unexpected response from USB command.');
		}
    }

    /// Parses a USB packet response from the mcu with sensor values (header TR1), and returns a list of all the values, from 0-255.
    /// Used internally, not normally needed for end users except for advanced use cases
    parseSensorValuePacket(data) {
        let values = []
		if (data.length > 3 && data[0] === 0x54 && data[1] === 0x52 && data[2] === TeledongCommands.GetSensorValues){
			let numSensors = data[3];

			for (let i = 0; i < numSensors; i++) {
				 if (4 + i >= data.length)
					break;
				values.push(data[4 + i] - 1);
			}
		}
        return values;
    }

}

// Enums for states and commands
const TeledongState = {
    NotConnected: 'NotConnected',
    Ok: 'Ok',
    Calibrating: 'Calibrating',
    Error: 'Error'
};

const TeledongCommands = {
    GetSensorValues: 0x01,
    GetFirmwareVersion: 0x02,
    SaveCalibrationValues: 0x03,
    LoadCalibrationValues: 0x04,
    SaveUserData: 0x05,
    ReadUserData: 0x06,
    SetSunlightMode: 0x07,
    GetSunlightMode: 0x08,
    EnterBootloader: 0xFE
};