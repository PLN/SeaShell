// ── Native asset E2E: SQLite on every platform ──
//
// Microsoft.Data.Sqlite pulls in SQLitePCLRaw.lib.e_sqlite3, which ships the
// SQLite engine only as runtimes/{rid}/native/(lib)e_sqlite3.{dll,so} — with
// a separate build for linux-musl-x64. Opening an in-memory database loads
// it, so this covers RID selection and flat native staging without a server.
//
// Before v0.6.1.264 this threw DllNotFoundException for e_sqlite3: the native
// file was staged under runtimes/{rid}/native/, which the host never probes
// for type:"project" libraries.
//
// Exit 0 = pass, non-zero = fail.

//sea_nuget Microsoft.Data.Sqlite 10.0.12

using System;
using Microsoft.Data.Sqlite;

var testName = "nuget-native-sqlite";

using var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('ok'); SELECT v || ' ' || sqlite_version() FROM t;";
var result = command.ExecuteScalar() as string;

if (result == null || !result.StartsWith("ok "))
{
	Console.Error.WriteLine($"[{testName}] FAIL: unexpected result '{result}'");
	return 1;
}

Console.WriteLine($"[{testName}] SQLite {result[3..]}");
Console.WriteLine($"[{testName}] PASS");
return 0;
