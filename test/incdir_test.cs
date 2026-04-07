//sea_incdir %ProgramData%/cs-script/inc
//sea_incdir ~/
//sea_inc Mother.cs
using Serilog;

Log.Information("incdir test — Mother loaded from %%ProgramData%% path");
Log.Information("Home dir expanded to: {Home}", $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}");

return 0;
