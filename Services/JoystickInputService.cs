using LandingRecorder.Models;
using SharpDX.DirectInput;

namespace LandingRecorder.Services;

public sealed class JoystickInputService : IDisposable
{
    private readonly List<DeviceInstance> _deviceInstances = [];
    private readonly List<string> _deviceNames = [];
    private DirectInput? _directInput;
    private Joystick? _gamepad;

    /// <summary>Names of the attached DirectInput devices (no "None" entry; that's a UI concept).</summary>
    public IReadOnlyList<string> DeviceNames => _deviceNames;

    /// <summary>Name of the selected device, or null when no joystick is selected (None).</summary>
    public string? SelectedDeviceName { get; private set; }

    /// <summary>Re-enumerates devices, then selects <paramref name="deviceName"/> (see <see cref="SelectDevice"/>).</summary>
    public void RefreshDevices(string? deviceName)
    {
        ReleaseGamepad();
        _deviceInstances.Clear();
        _deviceNames.Clear();

        try
        {
            _directInput ??= new DirectInput();
            _deviceInstances.AddRange(_directInput.GetDevices(DeviceType.Gamepad, DeviceEnumerationFlags.AllDevices));

            if (_deviceInstances.Count == 0)
            {
                _deviceInstances.AddRange(_directInput.GetDevices(DeviceType.Joystick, DeviceEnumerationFlags.AllDevices));
            }

            _deviceNames.AddRange(_deviceInstances.Select(device => device.ProductName ?? device.InstanceName));
        }
        catch (Exception ex)
        {
            _deviceInstances.Clear();
            _deviceNames.Clear();
            AppLogger.WriteLine($"Failed to enumerate joysticks: {ex.Message}");
        }

        SelectDevice(deviceName);
    }

    /// <summary>
    /// Selects the attached device with this name. Null selects None (joystick input disabled); a name
    /// that isn't attached also ends up as None rather than silently recording a different device.
    /// </summary>
    public void SelectDevice(string? deviceName)
    {
        ReleaseGamepad();
        SelectedDeviceName = null;

        if (deviceName is null)
        {
            return;
        }

        var index = _deviceNames.IndexOf(deviceName);
        if (index < 0)
        {
            AppLogger.WriteLine($"Joystick '{deviceName}' is not attached; joystick input disabled.");
            return;
        }

        try
        {
            _directInput ??= new DirectInput();
            _gamepad = new Joystick(_directInput, _deviceInstances[index].InstanceGuid);
            _gamepad.Properties.BufferSize = 128;
            _gamepad.Acquire();
            SelectedDeviceName = deviceName;
        }
        catch (Exception ex)
        {
            ReleaseGamepad();
            AppLogger.WriteLine($"Failed to acquire joystick '{deviceName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Polls the selected joystick once and writes each requested axis, as percent of full deflection, into
    /// <paramref name="values"/> under its <see cref="PlotVariable.JoystickRecordKey"/>. Raw 0..65535 maps
    /// to -100..+100 for every axis, keeping DirectInput's own direction (no inversion; a plot line inverts in
    /// its formula, e.g. "-x1"). Returns false (writing nothing) when no joystick is selected.
    /// </summary>
    public bool ReadAxes(IReadOnlyCollection<JoystickAxis> axes, IDictionary<string, double> values)
    {
        if (_gamepad is null)
        {
            return false;
        }

        if (axes.Count == 0)
        {
            return true;
        }

        JoystickState? state = null;
        try
        {
            _gamepad.Poll();
            state = _gamepad.GetCurrentState();
        }
        catch
        {
            // Transient read failure (e.g. device momentarily lost): record 0 to keep the series continuous.
        }

        foreach (var axis in axes)
        {
            values[PlotVariable.JoystickRecordKey(axis)] = state is null ? 0 : ToPercent(RawValue(state, axis));
        }

        return true;
    }

    /// <summary>
    /// Axes present on the attached device with this name (empty if it isn't attached). Uses the already
    /// acquired device when it's the selected one, otherwise opens it briefly just to enumerate its objects.
    /// </summary>
    public IReadOnlyList<JoystickAxis> GetAvailableAxes(string? deviceName)
    {
        var index = deviceName is null ? -1 : _deviceNames.IndexOf(deviceName);
        if (index < 0)
        {
            return [];
        }

        Joystick? temporary = null;
        try
        {
            var device = _gamepad is not null && deviceName == SelectedDeviceName
                ? _gamepad
                : temporary = new Joystick(_directInput ??= new DirectInput(), _deviceInstances[index].InstanceGuid);

            var axes = new List<JoystickAxis>();
            var sliderCount = 0;
            foreach (var deviceObject in device.GetObjects(DeviceObjectTypeFlags.AbsoluteAxis))
            {
                JoystickAxis? axis =
                    deviceObject.ObjectType == ObjectGuid.XAxis ? JoystickAxis.X
                    : deviceObject.ObjectType == ObjectGuid.YAxis ? JoystickAxis.Y
                    : deviceObject.ObjectType == ObjectGuid.ZAxis ? JoystickAxis.Z
                    : deviceObject.ObjectType == ObjectGuid.RxAxis ? JoystickAxis.RotationX
                    : deviceObject.ObjectType == ObjectGuid.RyAxis ? JoystickAxis.RotationY
                    : deviceObject.ObjectType == ObjectGuid.RzAxis ? JoystickAxis.RotationZ
                    : deviceObject.ObjectType == ObjectGuid.Slider ? (sliderCount++ == 0 ? JoystickAxis.Slider1 : JoystickAxis.Slider2)
                    : null;

                if (axis is not null && !axes.Contains(axis.Value))
                {
                    axes.Add(axis.Value);
                }
            }

            axes.Sort();
            return axes;
        }
        catch (Exception ex)
        {
            AppLogger.WriteLine($"Failed to enumerate axes of joystick '{deviceName}': {ex.Message}");
            return [];
        }
        finally
        {
            temporary?.Dispose();
        }
    }

    private static double ToPercent(int raw) => (raw / 65535.0) * 200.0 - 100.0;

    private static int RawValue(JoystickState state, JoystickAxis axis) => axis switch
    {
        JoystickAxis.X => state.X,
        JoystickAxis.Y => state.Y,
        JoystickAxis.Z => state.Z,
        JoystickAxis.RotationX => state.RotationX,
        JoystickAxis.RotationY => state.RotationY,
        JoystickAxis.RotationZ => state.RotationZ,
        JoystickAxis.Slider1 => state.Sliders.Length > 0 ? state.Sliders[0] : 0,
        JoystickAxis.Slider2 => state.Sliders.Length > 1 ? state.Sliders[1] : 0,
        _ => 0,
    };

    private void ReleaseGamepad()
    {
        try
        {
            _gamepad?.Unacquire();
            _gamepad?.Dispose();
        }
        catch
        {
            // Device may already be gone (unplugged); nothing more to release.
        }

        _gamepad = null;
    }

    public void Dispose()
    {
        ReleaseGamepad();
        _directInput?.Dispose();
        _directInput = null;
    }
}
