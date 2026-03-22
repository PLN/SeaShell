using System.IO;
using System.Runtime.InteropServices;

namespace SeaShell.ServiceHost;

/// <summary>Detected init system / service manager.</summary>
public enum InitSystem
{
	WindowsService,
	Systemd,
	Runit,
	OpenRc,
	Sysvinit,
	Unknown,
}

/// <summary>Auto-detect the platform's init system.</summary>
public static class InitSystemDetector
{
	public static InitSystem Detect()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return InitSystem.WindowsService;

		// systemd: definitive check for running systemd
		if (Directory.Exists("/run/systemd/system"))
			return InitSystem.Systemd;

		// runit: /etc/sv/ with supervise tooling
		if (Directory.Exists("/etc/sv") && (File.Exists("/usr/bin/sv") || File.Exists("/usr/sbin/sv")))
			return InitSystem.Runit;

		// OpenRC: presence of openrc or rc-status
		if (File.Exists("/sbin/openrc") || File.Exists("/sbin/rc-status"))
			return InitSystem.OpenRc;

		// sysvinit: fallback — most systems have /etc/init.d
		if (Directory.Exists("/etc/init.d"))
			return InitSystem.Sysvinit;

		return InitSystem.Unknown;
	}
}
