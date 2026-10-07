// ── RID-specific managed asset E2E: Microsoft.Data.SqlClient ──
//
// SqlClient's lib/ assembly is a stub that throws PlatformNotSupportedException;
// the real implementations ship as runtimes/win/lib/ and runtimes/unix/lib/.
// Linux RIDs only reach "unix" through the portable RID graph
// (linux-x64 → linux → unix-x64 → unix), which the resolver did not walk
// before, so every Linux script got the stub. On Windows, Open() also
// P/Invokes into the native SNI DLL (runtimes/win-x64/native/).
//
// Nothing listens on 127.0.0.1:1, so a working client fails with a
// SqlException from the network layer. Anything else is a staging failure.
//
// Exit 0 = pass, non-zero = fail.

//sea_nuget Microsoft.Data.SqlClient 6.1.5

using System;
using Microsoft.Data.SqlClient;

var testName = "nuget-runtime-sqlclient";

try
{
	using var connection = new SqlConnection(
		"Data Source=tcp:127.0.0.1,1;Connect Timeout=5;Encrypt=False;Pooling=False");
	connection.Open();
	Console.Error.WriteLine($"[{testName}] FAIL: connected to 127.0.0.1:1");
	return 1;
}
catch (SqlException ex)
{
	Console.WriteLine($"[{testName}] Network-layer SqlException as expected (error {ex.Number})");
}
catch (Exception ex)
{
	Console.Error.WriteLine($"[{testName}] FAIL: {ex.GetType().FullName}: {ex.Message}");
	return 1;
}

Console.WriteLine($"[{testName}] PASS");
return 0;
