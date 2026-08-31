// ── Native asset staging E2E: NuGet package with runtimes/{rid}/native/ ──
//
// Regression test for the native-asset layout bug: managed DLLs are staged
// flat (matching the type:"project" deps.json convention), but native assets
// used to be staged and declared at their runtimes/{rid}/native/ subpath.
// The host never adds that nested path to NATIVE_DLL_SEARCH_DIRECTORIES for
// project libraries, so the first P/Invoke threw DllNotFoundException even
// though the file was on disk.
//
// DacFx exercises the whole chain end to end: SqlConnection.Open P/Invokes
// into Microsoft.Data.SqlClient.SNI.dll (from the transitive
// Microsoft.Data.SqlClient.SNI.runtime package) before any network I/O, and
// the dacpac extract runs DacFx against a real database.
//
// Requires a local SQL Server with Windows auth (CI: the win-x64 runner).
// Usage: sea nuget-native-dacfx.cs [datasource]   (default: localhost)
// Exit 0 = pass, non-zero = fail.

//sea_nuget Microsoft.SqlServer.DacFx 170.5.76

using System;
using System.IO;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

var testName = "nuget-native-dacfx";
var dataSource = args.Length > 0 ? args[0] : "localhost";
var dbName = $"SeaShellNativeTest_{Environment.ProcessId}";
var connStr = $"Data Source={dataSource};Integrated Security=SSPI;Encrypt=True;TrustServerCertificate=True;";

Console.WriteLine($"[{testName}] Data source: {dataSource}");

// The native check happens right here — Open() P/Invokes into the SNI DLL
// before anything touches the network. With the old nested staging this
// line threw DllNotFoundException for Microsoft.Data.SqlClient.SNI.dll.
using var master = new SqlConnection(connStr);
master.Open();
Console.WriteLine($"[{testName}] Connected: SQL Server {master.ServerVersion}");

var dacpacPath = Path.Combine(Path.GetTempPath(), $"{dbName}.dacpac");
Exec(master, $"CREATE DATABASE [{dbName}]");
try
{
	using (var db = new SqlConnection(connStr + $"Initial Catalog={dbName};"))
	{
		db.Open();
		Exec(db, "CREATE TABLE dbo.SeaShellNativeTest (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(64) NOT NULL)");
	}

	var dac = new DacServices(connStr);
	dac.Extract(dacpacPath, dbName, testName, new Version(1, 0));

	var size = new FileInfo(dacpacPath).Length;
	if (size <= 0)
	{
		Console.Error.WriteLine($"[{testName}] FAIL: extracted dacpac is empty");
		return 1;
	}
	Console.WriteLine($"[{testName}] Extracted dacpac: {size} bytes");
}
finally
{
	try { Exec(master, $"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]"); } catch { }
	try { File.Delete(dacpacPath); } catch { }
}

Console.WriteLine($"[{testName}] PASS");
return 0;

static void Exec(SqlConnection c, string sql)
{
	using var cmd = c.CreateCommand();
	cmd.CommandText = sql;
	cmd.ExecuteNonQuery();
}
