using System;
using System.IO;
using System.Text;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Duplicates console writes into a second writer (the --log-file the parent
/// of a self-elevated install tails). Char-level is enough — Console writers
/// funnel everything through Write(char)/Write(string).
/// </summary>
internal sealed class TeeWriter : TextWriter
{
	private readonly TextWriter _a;
	private readonly TextWriter _b;

	public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }

	public override Encoding Encoding => _a.Encoding;

	public override void Write(char value) { _a.Write(value); _b.Write(value); }
	public override void Write(string? value) { _a.Write(value); _b.Write(value); }
	public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
	public override void Flush() { _a.Flush(); _b.Flush(); }
}