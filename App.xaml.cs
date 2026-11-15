using System.Configuration;
using System.Data;
using System.Runtime.InteropServices;
using System.Windows;
using SharpDX.MediaFoundation;

namespace LandingRecorder;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
	private bool _roInitialized;
	private bool _mfInitialized;

	protected override void OnStartup(StartupEventArgs e)
	{
		var result = RoInitialize(RoInitMultiThreaded);
		if (result is 0 or 1)
		{
			_roInitialized = true;
		}
		else if (result != RpcEChangedMode)
		{
			Marshal.ThrowExceptionForHR(result);
		}

		MediaFactory.Startup(MediaFactory.Version, 0);
		_mfInitialized = true;

		base.OnStartup(e);
	}

	protected override void OnExit(ExitEventArgs e)
	{
		if (_mfInitialized)
		{
			MediaFactory.Shutdown();
		}

		if (_roInitialized)
		{
			RoUninitialize();
		}

		base.OnExit(e);
	}

	private const uint RoInitSingleThreaded = 0;
	private const uint RoInitMultiThreaded = 1;
	private const int RpcEChangedMode = unchecked((int)0x80010106);

	[DllImport("combase.dll")]
	private static extern int RoInitialize(uint initType);

	[DllImport("combase.dll")]
	private static extern void RoUninitialize();
}

