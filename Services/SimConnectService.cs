using System.Runtime.InteropServices;
using LandingRecorder.Models;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingRecorder.Services;

/// <summary>
/// Owns the SimConnect connection and its connect/retry policy (retries every 5s on failure).
///
/// Two kinds of data are requested every visual frame:
/// - A fixed control definition (PLANE ALT ABOVE GROUND in feet, SIM ON GROUND) that's always monitored
///   for auto-capture/touchdown logic, regardless of plot-line settings. Exposed as typed properties;
///   it's not recorded unless a plot line also references those simvars.
/// - The configurable recorded variables (<see cref="SetRecordedVariables"/>), each registered as its own
///   single-FLOAT64 data definition. One definition per variable means an unrecognized simvar name only
///   loses that variable (SimConnect reports it via an exception, which is logged by name) instead of
///   breaking the whole block. Latest values are exposed via <see cref="GetRecordedValuesSnapshot"/>.
/// </summary>
public sealed class SimConnectService : IDisposable
{
    // SimConnect's managed API takes IDs as Enum; these are cast from arbitrary uints for the dynamic definitions.
    private enum DataId : uint
    {
        Control = 1,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ControlData
    {
        public double heightAboveGroundFeet;
        public double simOnGround;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SingleValue
    {
        public double value;
    }

    private const uint FirstDynamicId = 100;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly Dictionary<uint, string> _definitionNames = [];
    private readonly Dictionary<uint, string> _sentPacketDescriptions = [];
    private readonly Dictionary<string, double> _latestValues = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PlotVariable> _recordedVariables = [];
    private uint _nextDynamicId = FirstDynamicId;
    private IntPtr _ownerWindowHandle;
    private SimConnect? _simConnect;
    private DateTime _nextRetryUtc = DateTime.MinValue;
    // Failed attempts since the last successful connection (or start/disconnect). Only the first attempt of a
    // retry streak logs "initializing"/"failed", so a sim that isn't running doesn't spam app.log every 5s.
    private int _failedConnectAttempts;

    public bool IsConnected { get; private set; }

    public double HeightAboveGroundFeet { get; private set; }

    public bool SimOnGround { get; private set; }

    public event EventHandler<bool>? ConnectionStateChanged;

    public void Connect(IntPtr ownerWindowHandle)
    {
        _ownerWindowHandle = ownerWindowHandle;
        TryConnect();
    }

    /// <summary>
    /// Sets the simvars to request and record (joystick-axis variables are ignored). If connected,
    /// the previous dynamic definitions are cleared and the new ones registered immediately.
    /// </summary>
    public void SetRecordedVariables(IReadOnlyList<PlotVariable> variables)
    {
        _recordedVariables = variables.Where(variable => !variable.IsJoystickAxis && !string.IsNullOrEmpty(variable.Name)).ToList();
        if (_simConnect is null || !IsConnected)
        {
            return;
        }

        try
        {
            ClearDynamicDefinitions();
            RegisterDynamicDefinitions();
        }
        catch (Exception ex)
        {
            AppLogger.WriteLine($"Failed to re-register recorded simvars: {ex.Message}");
            Disconnect();
        }
    }

    /// <summary>Latest value of each recorded simvar that has delivered data since (re)registration.</summary>
    public Dictionary<string, double> GetRecordedValuesSnapshot() => new(_latestValues, StringComparer.OrdinalIgnoreCase);

    public void Pump()
    {
        if (IsConnected)
        {
            try
            {
                _simConnect?.ReceiveMessage();
            }
            catch (Exception ex)
            {
                AppLogger.WriteLine($"SimConnect receive failed: {ex.Message}");
                Disconnect();
            }
        }
        else if (DateTime.UtcNow >= _nextRetryUtc)
        {
            TryConnect();
        }
    }

    private void TryConnect()
    {
        try
        {
            if (_failedConnectAttempts == 0)
            {
                AppLogger.WriteLine("SimConnect initializing...");
            }

            _simConnect = new SimConnect("LandingRecorder", _ownerWindowHandle, 0x402, null, 0);
            _simConnect.OnRecvSimobjectData += OnRecvSimobjectData;
            _simConnect.OnRecvException += OnRecvException;

            AddDatum(DataId.Control, "PLANE ALT ABOVE GROUND", "feet");
            AddDatum(DataId.Control, "SIM ON GROUND", "Bool");
            _simConnect.RegisterDataDefineStruct<ControlData>(DataId.Control);
            _simConnect.RequestDataOnSimObject(DataId.Control, DataId.Control, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.VISUAL_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

            RegisterDynamicDefinitions();

            AppLogger.WriteLine(_failedConnectAttempts == 0
                ? "SimConnect initialized successfully."
                : $"SimConnect initialized successfully after {_failedConnectAttempts + 1} attempts.");
            _failedConnectAttempts = 0;
            SetConnected(true);
        }
        catch (Exception ex)
        {
            if (_failedConnectAttempts == 0)
            {
                AppLogger.WriteLine($"Failed to initialize SimConnect: {ex.Message} (retrying every {RetryDelay.TotalSeconds:0}s, further failures not logged)");
            }
            _failedConnectAttempts++;
            DisposeSimConnect();
            SetConnected(false);
            _nextRetryUtc = DateTime.UtcNow.Add(RetryDelay);
        }
    }

    private void RegisterDynamicDefinitions()
    {
        var simConnect = _simConnect!;
        foreach (var variable in _recordedVariables)
        {
            var id = _nextDynamicId++;
            var defineId = (DataId)id;
            AddDatum(defineId, variable.Name!, variable.Units ?? "");
            simConnect.RegisterDataDefineStruct<SingleValue>(defineId);
            simConnect.RequestDataOnSimObject(defineId, defineId, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.VISUAL_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            RememberLastPacket($"request for '{variable.Name}'");
            _definitionNames[id] = variable.Name!;
        }

        if (_recordedVariables.Count > 0)
        {
            AppLogger.WriteLine($"Recording simvars: {string.Join(", ", _recordedVariables.Select(v => $"{v.Name} [{v.Units}]"))}");
        }
    }

    private void ClearDynamicDefinitions()
    {
        var simConnect = _simConnect!;
        foreach (var id in _definitionNames.Keys)
        {
            var defineId = (DataId)id;
            simConnect.RequestDataOnSimObject(defineId, defineId, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            simConnect.ClearDataDefinition(defineId);
        }

        _definitionNames.Clear();
        _latestValues.Clear();
    }

    private void AddDatum(DataId defineId, string name, string units)
    {
        _simConnect!.AddToDataDefinition(defineId, name, units, SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
        RememberLastPacket($"'{name}' [{units}]");
    }

    /// <summary>Records what the last sent packet was, so a later SimConnect exception can name the culprit.</summary>
    private void RememberLastPacket(string description)
    {
        try
        {
            var sendId = _simConnect!.GetLastSentPacketID();
            _sentPacketDescriptions[sendId] = description;
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        var exception = (SIMCONNECT_EXCEPTION)data.dwException;
        var culprit = _sentPacketDescriptions.TryGetValue(data.dwSendID, out var description) ? description : $"packet {data.dwSendID}";
        AppLogger.WriteLine($"SimConnect exception {exception} for {culprit} (index {data.dwIndex}).");
    }

    private void Disconnect()
    {
        DisposeSimConnect();
        SetConnected(false);
        _nextRetryUtc = DateTime.UtcNow.Add(RetryDelay);
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        ConnectionStateChanged?.Invoke(this, connected);
    }

    private void DisposeSimConnect()
    {
        try
        {
            _simConnect?.Dispose();
        }
        catch
        {
            // Best-effort cleanup only.
        }

        _simConnect = null;
        _definitionNames.Clear();
        _sentPacketDescriptions.Clear();
        _latestValues.Clear();
    }

    private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwData is not { Length: > 0 })
        {
            return;
        }

        if (data.dwDefineID == (uint)DataId.Control)
        {
            var control = (ControlData)data.dwData[0];
            HeightAboveGroundFeet = control.heightAboveGroundFeet;
            SimOnGround = control.simOnGround != 0;
        }
        else if (_definitionNames.TryGetValue(data.dwDefineID, out var name))
        {
            _latestValues[name] = ((SingleValue)data.dwData[0]).value;
        }
    }

    public void Dispose() => DisposeSimConnect();
}
