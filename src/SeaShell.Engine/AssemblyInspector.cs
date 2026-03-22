using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SeaShell.Engine;

/// <summary>
/// Inspects pre-compiled .NET assemblies via PE metadata without loading them.
/// Used by CompileBinary to determine whether a binary references SeaShell.Script
/// and whether it targets ASP.NET Core.
/// </summary>
static class AssemblyInspector
{
	public sealed record AssemblyInfo(
		bool IsManaged,
		bool HasSeaShellRef,
		bool HasAspNetRef);

	public static AssemblyInfo Inspect(string assemblyPath)
	{
		try
		{
			using var stream = File.OpenRead(assemblyPath);
			using var peReader = new PEReader(stream);

			if (!peReader.HasMetadata)
				return new AssemblyInfo(false, false, false);

			var reader = peReader.GetMetadataReader();
			var hasSeaShell = false;
			var hasAspNet = false;

			foreach (var handle in reader.AssemblyReferences)
			{
				var asmRef = reader.GetAssemblyReference(handle);
				var name = reader.GetString(asmRef.Name);

				if (name.Equals("SeaShell.Script", StringComparison.OrdinalIgnoreCase))
					hasSeaShell = true;
				else if (name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
					hasAspNet = true;
			}

			return new AssemblyInfo(true, hasSeaShell, hasAspNet);
		}
		catch
		{
			return new AssemblyInfo(false, false, false);
		}
	}
}
