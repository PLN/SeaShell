// Symlink helper — check, grant, and test symlink operations on Windows.
//
// Usage:
//   sea test/symlink_helper.cs                 Show status
//   sea test/symlink_helper.cs check           Check privilege
//   sea test/symlink_helper.cs grant           Grant SeCreateSymbolicLinkPrivilege (requires elevation)
//   sea test/symlink_helper.cs revoke          Revoke SeCreateSymbolicLinkPrivilege (requires elevation)
//   sea test/symlink_helper.cs create <link> <target>   Create a file or directory symlink
//   sea test/symlink_helper.cs read <path>     Read symlink target
//   sea test/symlink_helper.cs test            Quick roundtrip test (create, read, delete)

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

// ---------------------------------------------------------------------------
// P/Invoke
// ---------------------------------------------------------------------------

[DllImport("advapi32.dll", SetLastError = true)]
static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);

[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);

[DllImport("advapi32.dll", SetLastError = true)]
static extern bool PrivilegeCheck(IntPtr token, ref PRIVILEGE_SET required, out bool result);

[DllImport("advapi32.dll", SetLastError = true)]
static extern uint LsaOpenPolicy(IntPtr system, ref LSA_OBJECT_ATTRIBUTES attr, uint access, out IntPtr policy);

[DllImport("advapi32.dll", SetLastError = true)]
static extern uint LsaAddAccountRights(IntPtr policy, IntPtr sid, LSA_UNICODE_STRING[] rights, uint count);

[DllImport("advapi32.dll", SetLastError = true)]
static extern uint LsaRemoveAccountRights(IntPtr policy, IntPtr sid, bool allRights, LSA_UNICODE_STRING[] rights, uint count);

[DllImport("advapi32.dll")]
static extern uint LsaClose(IntPtr handle);

[StructLayout(LayoutKind.Sequential)]
struct LUID { public uint LowPart; public int HighPart; }

[StructLayout(LayoutKind.Sequential)]
struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

[StructLayout(LayoutKind.Sequential)]
struct PRIVILEGE_SET
{
	public uint PrivilegeCount;
	public uint Control;
	public LUID_AND_ATTRIBUTES Privilege;
}

[StructLayout(LayoutKind.Sequential)]
struct LSA_OBJECT_ATTRIBUTES { public uint Length; public IntPtr a, b, c, d, e; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct LSA_UNICODE_STRING { public ushort Length; public ushort MaxLength; public string Buffer; }

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const string PrivName = "SeCreateSymbolicLinkPrivilege";

static bool HasSymlinkPrivilege()
{
	OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0008 /*TOKEN_QUERY*/, out var token);
	LookupPrivilegeValue(null, PrivName, out var luid);
	var ps = new PRIVILEGE_SET { PrivilegeCount = 1, Control = 1, Privilege = new() { Luid = luid } };
	PrivilegeCheck(token, ref ps, out var result);
	return result;
}

static bool IsElevated()
{
	using var identity = WindowsIdentity.GetCurrent();
	var principal = new WindowsPrincipal(identity);
	return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

static bool IsDeveloperMode()
{
	try
	{
		using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
			@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
		return key?.GetValue("AllowDevelopmentWithoutDevLicense") is int v && v == 1;
	}
	catch { return false; }
}

static IntPtr SidToPtr(SecurityIdentifier sid)
{
	var bytes = new byte[sid.BinaryLength];
	sid.GetBinaryForm(bytes, 0);
	var ptr = Marshal.AllocHGlobal(bytes.Length);
	Marshal.Copy(bytes, 0, ptr, bytes.Length);
	return ptr;
}

static LSA_UNICODE_STRING ToLsaString(string s) => new()
{
	Buffer    = s,
	Length    = (ushort)(s.Length * 2),
	MaxLength = (ushort)((s.Length + 1) * 2),
};

static uint ModifyPrivilege(SecurityIdentifier sid, bool grant)
{
	var attr = new LSA_OBJECT_ATTRIBUTES { Length = (uint)Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
	var status = LsaOpenPolicy(IntPtr.Zero, ref attr,
		0x000F0FFF /*POLICY_ALL_ACCESS*/, out var policy);
	if (status != 0) return status;

	var sidPtr = SidToPtr(sid);
	var rights = new[] { ToLsaString(PrivName) };

	status = grant
		? LsaAddAccountRights(policy, sidPtr, rights, 1)
		: LsaRemoveAccountRights(policy, sidPtr, false, rights, 1);

	Marshal.FreeHGlobal(sidPtr);
	LsaClose(policy);
	return status;
}

static bool TryCreateSymlink()
{
	var tmp    = Path.Combine(Path.GetTempPath(), $"symlink-test-{Guid.NewGuid():N}.txt");
	var link   = tmp + ".link";
	try
	{
		File.WriteAllText(tmp, "test");
		File.CreateSymbolicLink(link, tmp);
		return File.Exists(link);
	}
	catch { return false; }
	finally
	{
		try { File.Delete(link); } catch { }
		try { File.Delete(tmp); } catch { }
	}
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "status";

switch (cmd)
{
	case "status":
	case "check":
	{
		var user     = WindowsIdentity.GetCurrent();
		var hasPriv  = HasSymlinkPrivilege();
		var elevated = IsElevated();
		var devMode  = IsDeveloperMode();
		var canCreate = TryCreateSymlink();

		Console.WriteLine("=== Symlink Status ===");
		Console.WriteLine($"  User:              {user.Name}");
		Console.WriteLine($"  SID:               {user.User}");
		Console.WriteLine($"  Elevated:          {elevated}");
		Console.WriteLine($"  Developer Mode:    {devMode}");
		Console.WriteLine($"  {PrivName}:");
		Console.WriteLine($"    In token:        {hasPriv}");
		Console.WriteLine($"  Can create symlink: {canCreate}");
		Console.WriteLine();
		if (canCreate)
			Console.WriteLine("  Ready to create symlinks.");
		else if (hasPriv)
			Console.WriteLine("  Privilege is in token but creation still fails — unexpected.");
		else
			Console.WriteLine("  Cannot create symlinks. Use 'grant' (elevated) or enable Developer Mode.");
		return 0;
	}

	case "grant":
	{
		if (!IsElevated())
		{
			Console.Error.WriteLine("  ERROR: Grant requires elevation. Use gsudo.");
			return 1;
		}
		var sid = WindowsIdentity.GetCurrent().User!;
		Console.WriteLine($"  Granting {PrivName} to {sid}...");
		Console.WriteLine($"  SID: {sid.Value}");
		var status = ModifyPrivilege(sid, grant: true);
		if (status == 0)
		{
			Console.WriteLine("  Granted. Log off and back on for it to take effect.");
			return 0;
		}
		Console.Error.WriteLine($"  ERROR: LsaAddAccountRights returned 0x{status:X8}");
		return 1;
	}

	case "revoke":
	{
		if (!IsElevated())
		{
			Console.Error.WriteLine("  ERROR: Revoke requires elevation. Use gsudo.");
			return 1;
		}
		var sid = WindowsIdentity.GetCurrent().User!;
		Console.WriteLine($"  Revoking {PrivName} from {sid}...");
		var status = ModifyPrivilege(sid, grant: false);
		if (status == 0)
		{
			Console.WriteLine("  Revoked. Log off and back on for it to take effect.");
			return 0;
		}
		Console.Error.WriteLine($"  ERROR: LsaRemoveAccountRights returned 0x{status:X8}");
		return 1;
	}

	case "create":
	{
		if (args.Length < 3)
		{
			Console.Error.WriteLine("  Usage: symlink_helper.cs create <link> <target>");
			return 1;
		}
		var linkPath   = Path.GetFullPath(args[1]);
		var targetPath = Path.GetFullPath(args[2]);

		if (Directory.Exists(targetPath))
			Directory.CreateSymbolicLink(linkPath, targetPath);
		else if (File.Exists(targetPath))
			File.CreateSymbolicLink(linkPath, targetPath);
		else
		{
			Console.Error.WriteLine($"  ERROR: Target does not exist: {targetPath}");
			return 1;
		}

		Console.WriteLine($"  Created: {linkPath} -> {targetPath}");
		return 0;
	}

	case "read":
	{
		if (args.Length < 2)
		{
			Console.Error.WriteLine("  Usage: symlink_helper.cs read <path>");
			return 1;
		}
		var path = Path.GetFullPath(args[1]);
		var info = new FileInfo(path);
		if (!info.Exists && !Directory.Exists(path))
		{
			Console.Error.WriteLine($"  ERROR: Path does not exist: {path}");
			return 1;
		}

		var linkTarget = info.LinkTarget ?? new DirectoryInfo(path).LinkTarget;
		if (linkTarget != null)
		{
			var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
				?? Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
			Console.WriteLine($"  Path:      {path}");
			Console.WriteLine($"  Target:    {linkTarget}");
			Console.WriteLine($"  Resolved:  {resolved ?? "(could not resolve)"}");
		}
		else
		{
			Console.WriteLine($"  Path:      {path}");
			Console.WriteLine($"  Not a symbolic link.");
		}
		return 0;
	}

	case "test":
	{
		var tmpDir  = Path.Combine(Path.GetTempPath(), $"symlink-roundtrip-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tmpDir);

		var targetFile = Path.Combine(tmpDir, "target.txt");
		var fileLink   = Path.Combine(tmpDir, "file.link");
		var targetDir  = Path.Combine(tmpDir, "targetdir");
		var dirLink    = Path.Combine(tmpDir, "dir.link");

		try
		{
			// File symlink
			File.WriteAllText(targetFile, "hello symlink");
			Console.Write("  File symlink create... ");
			File.CreateSymbolicLink(fileLink, targetFile);
			Console.WriteLine("OK");

			Console.Write("  File symlink read...   ");
			var content = File.ReadAllText(fileLink);
			Console.WriteLine(content == "hello symlink" ? "OK" : $"MISMATCH: {content}");

			Console.Write("  File symlink target... ");
			var ft = new FileInfo(fileLink).LinkTarget;
			Console.WriteLine(ft != null ? $"OK ({ft})" : "FAIL (null)");

			Console.Write("  File symlink resolve.. ");
			var fr = File.ResolveLinkTarget(fileLink, true)?.FullName;
			Console.WriteLine(fr != null ? $"OK ({fr})" : "FAIL (null)");

			// Directory symlink
			Directory.CreateDirectory(targetDir);
			File.WriteAllText(Path.Combine(targetDir, "marker.txt"), "dir-marker");
			Console.Write("  Dir symlink create...  ");
			Directory.CreateSymbolicLink(dirLink, targetDir);
			Console.WriteLine("OK");

			Console.Write("  Dir symlink read...    ");
			var dc = File.ReadAllText(Path.Combine(dirLink, "marker.txt"));
			Console.WriteLine(dc == "dir-marker" ? "OK" : $"MISMATCH: {dc}");

			Console.Write("  Dir symlink target...  ");
			var dt = new DirectoryInfo(dirLink).LinkTarget;
			Console.WriteLine(dt != null ? $"OK ({dt})" : "FAIL (null)");

			Console.WriteLine();
			Console.WriteLine("  All symlink operations working.");
			return 0;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"FAIL: {ex.Message}");
			return 1;
		}
		finally
		{
			try { Directory.Delete(tmpDir, true); } catch { }
		}
	}

	default:
		Console.Error.WriteLine($"  Unknown command: {cmd}");
		Console.Error.WriteLine("  Commands: status, check, grant, revoke, create, read, test");
		return 1;
}
