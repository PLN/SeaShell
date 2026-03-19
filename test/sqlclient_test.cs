//sea_nuget Microsoft.Data.SqlClient
using Microsoft.Data.SqlClient;

var csb = new SqlConnectionStringBuilder
{
	DataSource = @"(local)",
	InitialCatalog = "master",
	IntegratedSecurity = true,
	TrustServerCertificate = true,
};

using var conn = new SqlConnection(csb.ConnectionString);
conn.Open();
Console.WriteLine($"Connected to: {conn.DataSource} / {conn.Database}");

using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT @@VERSION";
var version = (string)cmd.ExecuteScalar()!;
Console.WriteLine($"SQL Server: {version.Split('\n')[0].Trim()}");

return 0;
