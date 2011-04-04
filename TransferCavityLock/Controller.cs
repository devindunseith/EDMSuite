﻿using System;
using System.Threading;
using NationalInstruments.DAQmx;
using DAQ.TransferCavityLock;
using DAQ.Environment;
using DAQ.HAL;
using System.Windows.Forms;
using NationalInstruments.Analysis.Math;

namespace TransferCavityLock
{
    /// <summary>
    /// A class for locking the laser using a transfer cavity.
    /// </summary>
    public class Controller : MarshalByRefObject
    {

        #region Declarations

        private const double UPPER_LC_VOLTAGE_LIMIT = 10.0; //volts LC: Laser control
        private const double LOWER_LC_VOLTAGE_LIMIT = -10.0; //volts LC: Laser control
        private const double UPPER_CC_VOLTAGE_LIMIT = 10.0; //volts CC: Cavity control
        private const double LOWER_CC_VOLTAGE_LIMIT = 0; //volts CC: Cavity control

        private const double TWEAK_GAIN = 0.001;
        public int Increments = 0;          // for tweaking the laser set point
        public int Decrements = 0;

        public const int default_ScanPoints = 100;
        public const double default_ScanOffset = 3.0;
        public const double default_ScanWidth = 0.3;
        public const double default_Gain = 0.0;
        public const double default_VoltageToLaser = 0.0;



        private MainForm ui;

        private TransferCavityLockable tcl = 
            (TransferCavityLockable)Activator.GetObject(typeof(TransferCavityLockable), "http://localhost:1172");

        public enum ControllerState
        {
            STOPPED, FREERUNNING, CAVITYSTABILIZED, LASERLOCKING, LASERLOCKED
        };
        public ControllerState State = ControllerState.STOPPED;

        public object rampStopLock = new object();
        public object tweakLock = new object();

        // without this method, any remote connections to this object will time out after
        // five minutes of inactivity.
        // It just overrides the lifetime lease system completely.
        public override Object InitializeLifetimeService()
        {
            return null;
        }

        #endregion

        #region Setup

        public void Start()
        {
            ui = new MainForm();
            ui.controller = this;
            
            Application.Run(ui);
            State = ControllerState.STOPPED;
        }

        #endregion

        #region Public methods

        //This gets called from the form_load atm. ask Jony to see if this can be changed.
        //putting it in start doesn't seem to do anything.
        public void InitializeUI()
        {
            setUIInitialValues();
            ui.updateUIState(State);
        }

        public void StartRamp()
        {
            State = Controller.ControllerState.FREERUNNING;
            Thread.Sleep(2000);
            Thread rampThread = new Thread(new ThreadStart(rampLoop));
            ui.updateUIState(State);
            
            rampThread.Start();
        }

        public void StopRamp()
        {
            State = ControllerState.STOPPED;
            ui.updateUIState(State); 
        }

        public void EngageLock()
        {
            State = ControllerState.LASERLOCKING;
            ui.updateUIState(State);
        }
        public void DisengageLock()
        {
            State = ControllerState.CAVITYSTABILIZED;
            ui.updateUIState(State);
        }
        public void StabilizeCavity()
        {
            State = ControllerState.CAVITYSTABILIZED;
            ui.updateUIState(State);
        }
        public void UnlockCavity()
        {
            State = ControllerState.FREERUNNING;
            ui.updateUIState(State);
        }

        private int numberOfPoints;
        private double scanWidth;

        private double voltageToLaser;
        public double VoltageToLaser
        {
            get
            {
                return voltageToLaser;
            }
            set
            {
                voltageToLaser = value;
                ui.SetLaserVoltage(voltageToLaser);
            }
        }
        internal void WindowVoltageToLaserChanged(double voltage)
        {
            voltageToLaser = voltage;
        }
        
        private double gain;
        public double Gain
        {
            get
            {
                return gain;
            }
            set
            {
                gain = value;
                ui.SetGain(gain);
            }
        }
        internal void WindowGainChanged(double g)
        {
            gain = g;
        }

        private double scanOffset;
        public double ScanOffset
        {
            get
            {
                return scanOffset;
            }
            set
            {
                scanOffset = value;
                ui.SetScanOffset(scanOffset);
            }
        }
        private double laserSetPoint;
        public double LaserSetPoint
        {
            get 
            { 
                return laserSetPoint; 
            }
            set
            {
                laserSetPoint = value;
                ui.SetLaserSetPoint(laserSetPoint);
            }
        }


        #endregion

        #region Private methods

        // This sets the initial values into the various boxes on the UI.
        private void setUIInitialValues()
        {
            ui.SetScanOffset(default_ScanOffset);
            ui.SetScanWidth(default_ScanWidth);
            ui.SetLaserVoltage(default_VoltageToLaser);
            ui.SetGain(default_Gain);
            ui.SetNumberOfPoints(default_ScanPoints);
        }

        /// <summary>
        /// A function to scan across the voltage range set by the limits high and low. 
        /// Reads from the two photodiodes and spits out an array.
        /// The basic unit of the program.
        /// </summary>
        private CavityScanData scan(ScanParameters sp)
        {
            CavityScanData scanData = new CavityScanData(sp.Steps);
            scanData.parameters = sp;

            double[] voltages = sp.CalculateRampVoltages();

            tcl.ScanCavity(voltages, false);
            tcl.StartReadingPhotodiodes();
            tcl.StartCavityScan();


            Thread.Sleep(10);
            tcl.ScanAndWait();

            scanData.PhotodiodeData = tcl.ReadPhotodiodes(sp.Steps);

            tcl.StopCavityScan();
            tcl.StopReadingPhotodiodes();

            return scanData;
        }


        /// <summary>
        /// The main loop. Scans the cavity, looks at photodiodes, corrects the cavity scan range for the next
        /// scan and locks the laser.
        /// It does a first scan of the data before starting.
        /// It then enters a loop where the next scan is prepared. The preparation varies depending on 
        /// the ControllerState. Once all the preparation is done, the next scan is started. And so on.
        /// </summary>
        private void rampLoop()
        {
            readParametersFromUI();
            ScanParameters sp = createInitialScanParameters();
            initializeHardware();
            CavityScanData scanData = scan(sp);

            while (State != ControllerState.STOPPED)
            {
                displayData(sp, scanData);

                double[] masterDataFit = CavityScanFitter.FitLorenzianToMasterData(scanData, sp.Low, sp.High);
                double[] slaveDataFit = CavityScanFitter.FitLorenzianToMasterData(scanData, sp.Low, sp.High);

                switch (State)
                {
                    case ControllerState.FREERUNNING:
                        break;

                    case ControllerState.CAVITYSTABILIZED:
                        calculateNewScanRange(sp, masterDataFit);
                        break;

                    case ControllerState.LASERLOCKING:
                        calculateNewScanRange(sp, masterDataFit);

                        LaserSetPoint = CalculateLaserSetPoint(masterDataFit, slaveDataFit);

                        State = ControllerState.LASERLOCKED;
                        ui.updateUIState(State);
                        break;

                    case ControllerState.LASERLOCKED:
                        calculateNewScanRange(sp, masterDataFit);

                        LaserSetPoint = tweakSetPoint(LaserSetPoint); //does nothing if not tweaked

                        double shift = calculateDeviationFromSetPoint(LaserSetPoint, slaveDataFit, masterDataFit);
                        VoltageToLaser = calculateNewVoltageToLaser(shift, VoltageToLaser);

                        break;

                }
                tcl.SetLaserVoltage(VoltageToLaser);

                scanData = scan(sp);
            }

            finalizeRamping();
        }


        private void displayData(ScanParameters sp, CavityScanData data)
        {
            ui.ScatterGraphPlot(ui.MasterLaserIntensityScatterGraph, sp.CalculateRampVoltages(), data.SlavePhotodiodeData);
            ui.ScatterGraphPlot(ui.SlaveLaserIntensityScatterGraph, sp.CalculateRampVoltages(), data.MasterPhotodiodeData);
        }

        /// <summary>
        /// Gets some parameters from the UI and stores them on the controller.
        /// </summary>
        private void readParametersFromUI()
        {
            // read out UI params
            numberOfPoints = ui.GetNumberOfPoints();
            scanWidth = ui.GetScanWidth();
            scanOffset = ui.GetScanOffset();
        }
        

        private ScanParameters createInitialScanParameters()
        {
            ScanParameters sp = new ScanParameters();
            sp.Steps = numberOfPoints;
            sp.Low = ScanOffset - (0.5 * scanWidth);
            sp.High = ScanOffset + (0.5 * scanWidth);
            sp.SleepTime = 0;

            return sp;
        }

        private void finalizeRamping()
        {
            VoltageToLaser = 0.0;
            tcl.SetLaserVoltage(0.0);
            tcl.ReleaseHardwareControl();
        }

        private void initializeHardware()
        {
            tcl.ConfigureCavityScan(numberOfPoints, false);
            tcl.ConfigureReadPhotodiodes(numberOfPoints, false);
            tcl.ConfigureScanTrigger();
            tcl.ConfigureSetLaserVoltage(VoltageToLaser);
        }
        
        /// <summary>
        /// This adjusts the scan range of the next scan, so that the HeNe peak stays in the middle of the scan.
        /// It modifies the scan parameters that are passed to it.
        /// </summary>
        private void calculateNewScanRange(ScanParameters scanParameters, double[] masterPDFitCoefficients)
        {
             if (masterPDFitCoefficients[1] - scanWidth > LOWER_CC_VOLTAGE_LIMIT
                && masterPDFitCoefficients[1] + scanWidth < UPPER_CC_VOLTAGE_LIMIT
                && masterPDFitCoefficients[1] + scanWidth < scanParameters.High
                && masterPDFitCoefficients[1] - scanWidth > scanParameters.Low) //Only change limits if fits are reasonable.
            {
                scanParameters.High = masterPDFitCoefficients[1] + scanWidth;//Adjust scan range!
                scanParameters.Low = masterPDFitCoefficients[1] - scanWidth;
            }
        }

        /// <summary>
        /// Measures the laser set point (the distance between the he-ne and TiS peaks in cavity voltage units)
        /// The lock (see calculateDeviationFromSetPoint) will adjust the voltage fed to the TiS to keep this number constant.
        /// </summary>     

        private double CalculateLaserSetPoint(double[] MasterPDFitCoefficients, double[] SlavePDFitCoefficients)
        {
            double setPoint = new double();
            if (SlavePDFitCoefficients[1] > LOWER_CC_VOLTAGE_LIMIT
               && SlavePDFitCoefficients[1] < UPPER_CC_VOLTAGE_LIMIT) //Only change limits if fits are reasonable.
            {
                setPoint = Math.Round(SlavePDFitCoefficients[1] - MasterPDFitCoefficients[1], 4);
            }
            else
            {
                setPoint = 0.0;
            }
            return setPoint;

        }

        private double tweakSetPoint(double oldSetPoint)
        {
            double newSetPoint = oldSetPoint + TWEAK_GAIN * (Increments - Decrements); //
            Increments = 0;
            Decrements = 0;
            return newSetPoint;
        }

        private double calculateDeviationFromSetPoint(double laserSetPoint, 
            double[] SlavePDFitCoefficients, double[] MasterPDFitCoefficients)
        {
            double currentPeakSeparation = new double();

            if (SlavePDFitCoefficients[1] > LOWER_CC_VOLTAGE_LIMIT
                && SlavePDFitCoefficients[1] < UPPER_CC_VOLTAGE_LIMIT) //Only change limits if fits are reasonable.
            {
                currentPeakSeparation = Math.Round(SlavePDFitCoefficients[1]
                    - MasterPDFitCoefficients[1], 4);
            }
            else
            {
                currentPeakSeparation = laserSetPoint;
            }

            return Math.Round(currentPeakSeparation - laserSetPoint, 4);

            

        }

        private double calculateNewVoltageToLaser(double VoltageToLaser, double measuredVoltageChange)
        {
            double newVoltage;
            if (VoltageToLaser
                - Gain * measuredVoltageChange > UPPER_LC_VOLTAGE_LIMIT
                || VoltageToLaser
                - Gain * measuredVoltageChange < LOWER_LC_VOLTAGE_LIMIT)
            {
                newVoltage = VoltageToLaser;
            }
            else
            {
                newVoltage = VoltageToLaser - Gain * measuredVoltageChange; //Feedback 
            }
            return Math.Round(newVoltage, 4);
        }

        #endregion

        

    }




}
