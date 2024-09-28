## Software library integration guide for the Teledong

#### How the Teledong works

The Teledong uses an array of photosensors with infrared LEDs along the shaft to optically detect how much of the shaft is obscured by measuring the reflected infrared light. For example, if the shaft is halfway into an orifice, half of the photosensors will return a value indicating that they are obscured, and the other half will return a value indicating that there is nothing in front of them. These sensor values can be read by software over USB.

This means that in order for your software to obtain a single value representing how deep the Teledong is inserted (the "position"), it has to combine the readings of all the individual sensors in the array in some manner. In the reference software libraries, this is done for you in the GetPosition() functions, but it is worth being aware of how this works under the hood, because it has a few implications. 

#### Calibration

Because the photosensors read light, the device needs to be calibrated to the surrounding lighting conditions in order to work correctly. It comes pre-calibrated to average indoor ambient lighting, however in bright conditions, such as with sunlight coming in windows, the user will need to perform a new calibration in order for GetPosition() to return the correct value. 

Everything that has to do with calibration is done in software rather than in the device itself. Calibration values from the previous calibration is stored in the memory of the device and can be read by the software library, which is then used by GetPosition() to calculate the final position value. Therefore it is mandatory for users of the library to at least call LoadCalibration() before using the device, to load this previously valid calibration. But it is also highly recommended to implement an interface for the user to initiate a new calibration routine. More on this later in this document. 

It is worth noting that in case the ambient lighting is very bright, bright enough to overpower the infrared light from the built-in LEDs, the library can turn off these LEDs and rely solely on the sensors detecting ambient light (or the lack of it) to determine whether sensors are obscured, inverting the usual expected values for obscured/unobscured sensors. This is called Sunlight Mode. The reference software library handles this automatically, but it is worth being aware of.


#### Minimum sample usage

When initiating a session, first connect to the Teledong using *Connect()*. 

Then, load the previous/default calibration using *LoadCalibration()*.

Set up a timer that runs periodically, once every 50 ms is suggested. On every cycle, run *GetPosition()*. This returns a value representing how deep the position of the Teledong is, from 1.0 = Nothing on the dildo, to 0.0 = Dildo fully inserted.

When the session is done, call *Disconnect()*.

In addition, implementing a calibration routing and other user interface features is highly recommended. More on this in the next section:


#### Recommended user interface

While it is possible to read values from the Teledong without any graphical interface, a user interface with certain indications and controls is highly recommended to provide the best experience for the user.

The most important such feature is to ability for the user to initiate a new calibration routine. An example basic interface would be a button that says "Calibrate Teledong", and a text label that indicates the status (whether a calibration is currently ongoing). The code for running a calibration routine is available in the reference software library in the *Calibrate()* / *CalibrateAsync()* function. When running this function, the user should stimulate the shaft to all possible depths, for example by dragging their hand along the shaft up and down repeatedly, to simulate insertion, over a course of 10 seconds by default. A good user interface should also indicate to the user how this works, for example with a small illustration/animation or text prompt telling the user what to do. 

The State variable in the reference library can return whether a calibration is running or not, as well as whether the Teledong is connected or not. 

A good user interface should also indicate to the user if it detects sensors readings far outside the expected range, suggesting that the current calibration is no good and that a new calibration should be performed. In the reference software libraries, the BadCalibrationWarning variable represents such a warning. 

In the reference library, there is one more feature that should be able to be toggled on or off by the user, for example with a checkbox control. It is the variable KeepPositionAtRelease. This toggles whether or not the library should attempt to "freeze" the position to the last valid one, in the case that the user releases the grip on the dildo mid-stroke, applicable during handjobs. If this is turned off, such a motion would instantly cause the position to return to 1.0, leading to very jarring stroker movement if the Teledong is acting as a remote controller. However if this is not applicable, it is best to turn off this feature, as very quick strokes could sometimes lead to false positives. 

#### Teledong Commander

For more reference code on how the Teledong can be used, check out the Teledong Commander software in this repo, implementing support for the Teledong acting as a remote controller for strokers as well as recording to funscripts.

#### License

The reference software libraries have the MIT License, meaning you are free to use them in your software, even commercial ones, as long as you include a copy of the MIT License text somewhere. 