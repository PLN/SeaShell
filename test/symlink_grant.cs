//sea_elevate
// Grant or revoke SeCreateSymbolicLinkPrivilege for the current user.
// Usage:  sea test/symlink_grant.cs          (grant)
//         sea test/symlink_grant.cs revoke   (revoke)

using System.Runtime.InteropServices;
using System.Security.Principal;

[DllImport("advapi32.dll", SetLastError = true)]
static extern uint LsaOpenPolicy(IntPtr system, ref LSA_OBJECT_ATTRIBUTES attr, uint access, out IntPtr policy);

[DllImport("advapi32.dll", SetLastError = true)]
static extern uint LsaAddAccountRights(IntPtr policy, IntPtr sid, LSA_UNICODE_STRING[] rights, uint count);

[DllImport("advapi32.dll", SetLastError = true)]
static extern uint LsaRemoveAccountRights(IntPtr policy, IntPtr sid, bool allRights, LSA_UNICODE_STRING[] rights, uint count);

[DllImport("advapi32.dll")]
static extern uint LsaClose(IntPtr handle);

[StructLayout(LayoutKind.Sequential)]
struct LSA_OBJECT_ATTRIBUTES { public uint Length; public IntPtr a, b, c, d, e; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct LSA_UNICODE_STRING { public ushort Length; public ushort MaxLength; public string Buffer; }

var logFile = Path.Combine(Path.GetTempPath(), "symlink_grant.log");
void LogLine(string msg) { File.AppendAllText(logFile, msg + "\n"); Console.WriteLine(msg); }
File.WriteAllText(logFile, "");

var revoke = args.Length > 0 && args[0].Equals("revoke", StringComparison.OrdinalIgnoreCase);
var priv   = "SeCreateSymbolicLinkPrivilege";
LogLine($"Action: {(revoke ? "revoke" : "grant")}");
LogLine($"Elevated: {Sea.IsElevated}");

var sid = WindowsIdentity.GetCurrent().User!;
LogLine($"SID: {sid.Value}");
var sidBytes = new byte[sid.BinaryLength];
sid.GetBinaryForm(sidBytes, 0);
var sidPtr = Marshal.AllocHGlobal(sidBytes.Length);
Marshal.Copy(sidBytes, 0, sidPtr, sidBytes.Length);

var attr = new LSA_OBJECT_ATTRIBUTES { Length = (uint)Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
// 0x000F0FFF = POLICY_ALL_ACCESS; 0x0802 was insufficient for LsaAddAccountRights
var status = LsaOpenPolicy(IntPtr.Zero, ref attr, 0x000F0FFF, out var policy);
LogLine($"LsaOpenPolicy: 0x{status:X8}");
if (status != 0) return 1;

var rights = new[] { new LSA_UNICODE_STRING
{
	Buffer    = priv,
	Length    = (ushort)(priv.Length * 2),
	MaxLength = (ushort)((priv.Length + 1) * 2),
}};

status = revoke
	? LsaRemoveAccountRights(policy, sidPtr, false, rights, 1)
	: LsaAddAccountRights(policy, sidPtr, rights, 1);

LogLine($"Lsa{(revoke ? "Remove" : "Add")}AccountRights: 0x{status:X8}");

Marshal.FreeHGlobal(sidPtr);
LsaClose(policy);

LogLine(status == 0 ? "Success." : "FAILED.");
return status == 0 ? 0 : 1;
