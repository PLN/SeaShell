using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SeaShell.Ipc;

namespace SeaShell;

#pragma warning disable CS1591 // Missing XML comment — mirrors System.Console API surface

/// <summary>
/// Console proxy for elevated scripts. When injected via
/// <c>global using Console = SeaShell.SeaConsole;</c>, this class
/// transparently relays all console I/O through the IPC pipe to the CLI
/// process, which executes the operations on its real terminal.
///
/// When relay is not enabled (non-elevated, or AttachConsole succeeded),
/// all members delegate directly to <see cref="System.Console"/>.
/// </summary>
public static class SeaConsole
{
	// ── Console control op codes ────────────────────────────────────────

	internal static class Op
	{
		public const byte SetTitle          = 0;
		public const byte GetTitle          = 1;
		public const byte SetForeground     = 2;
		public const byte GetForeground     = 3;
		public const byte SetBackground     = 4;
		public const byte GetBackground     = 5;
		public const byte ResetColor        = 6;
		public const byte SetCursorPosition = 7;
		public const byte GetCursorLeft     = 8;
		public const byte GetCursorTop      = 9;
		public const byte SetCursorVisible  = 10;
		public const byte GetCursorVisible  = 19;
		public const byte GetBufferWidth    = 11;
		public const byte GetBufferHeight   = 12;
		public const byte GetWindowWidth    = 13;
		public const byte GetWindowHeight   = 14;
		public const byte Clear             = 15;
		public const byte Beep              = 16;
		public const byte ReadKey           = 17;
		public const byte GetKeyAvailable   = 18;
	}

	// ── Relay state ─────────────────────────────────────────────────────

	private static MessageChannel? _channel;
	private static RelayTextWriter? _relayOut;
	private static RelayTextWriter? _relayErr;
	private static RelayTextReader? _relayIn;

	// Request/response synchronization: only one request-response can be
	// in flight at a time (the script thread blocks until the CLI replies).
	private static readonly SemaphoreSlim _requestLock = new(1, 1);
	private static readonly SemaphoreSlim _responseReady = new(0, 1);
	private static byte[]? _responseData;

	/// <summary>Whether the pipe relay is active.</summary>
	public static bool IsRelayEnabled => _channel != null;

	/// <summary>Enable pipe relay mode. Called by Sea.Initialize when SEASHELL_CLI_PID is set.</summary>
	internal static void EnableRelay(MessageChannel channel)
	{
		_channel  = channel;
		_relayOut = new RelayTextWriter(channel, 0);
		_relayErr = new RelayTextWriter(channel, 1);
		_relayIn  = new RelayTextReader(channel);
	}

	/// <summary>Deliver a response from the CLI (called by Sea.ReceiveLoop).</summary>
	internal static void DeliverStdinLine(byte[] data)
	{
		_relayIn?.DeliverLine(data);
	}

	/// <summary>Deliver a ConsoleControl response (called by Sea.ReceiveLoop).</summary>
	internal static void DeliverControlResponse(byte[] data)
	{
		_responseData = data;
		try { _responseReady.Release(); } catch (SemaphoreFullException) { }
	}

	// ── Streams ─────────────────────────────────────────────────────────

	public static TextWriter Out   => _relayOut ?? System.Console.Out;
	public static TextWriter Error => _relayErr ?? System.Console.Error;
	public static TextReader In    => _relayIn  ?? (TextReader)System.Console.In;

	public static void SetOut(TextWriter writer)   { if (_channel == null) System.Console.SetOut(writer); }
	public static void SetError(TextWriter writer) { if (_channel == null) System.Console.SetError(writer); }
	public static void SetIn(TextReader reader)    { if (_channel == null) System.Console.SetIn(reader); }

	public static Stream OpenStandardInput()  => System.Console.OpenStandardInput();
	public static Stream OpenStandardOutput() => System.Console.OpenStandardOutput();
	public static Stream OpenStandardError()  => System.Console.OpenStandardError();

	// ── Write ───────────────────────────────────────────────────────────

	public static void Write(bool value)                          => Out.Write(value);
	public static void Write(char value)                          => Out.Write(value);
	public static void Write(char[]? buffer)                      => Out.Write(buffer);
	public static void Write(char[] buffer, int index, int count) => Out.Write(buffer, index, count);
	public static void Write(decimal value)                       => Out.Write(value);
	public static void Write(double value)                        => Out.Write(value);
	public static void Write(float value)                         => Out.Write(value);
	public static void Write(int value)                           => Out.Write(value);
	public static void Write(uint value)                          => Out.Write(value);
	public static void Write(long value)                          => Out.Write(value);
	public static void Write(ulong value)                         => Out.Write(value);
	public static void Write(object? value)                       => Out.Write(value);
	public static void Write(string? value)                       => Out.Write(value);
	public static void Write(string format, object? arg0)                            => Out.Write(format, arg0);
	public static void Write(string format, object? arg0, object? arg1)              => Out.Write(format, arg0, arg1);
	public static void Write(string format, object? arg0, object? arg1, object? arg2) => Out.Write(format, arg0, arg1, arg2);
	public static void Write(string format, params object?[] arg)                    => Out.Write(format, arg);

	public static void WriteLine()                                          => Out.WriteLine();
	public static void WriteLine(bool value)                                => Out.WriteLine(value);
	public static void WriteLine(char value)                                => Out.WriteLine(value);
	public static void WriteLine(char[]? buffer)                            => Out.WriteLine(buffer);
	public static void WriteLine(char[] buffer, int index, int count)       => Out.WriteLine(buffer, index, count);
	public static void WriteLine(decimal value)                             => Out.WriteLine(value);
	public static void WriteLine(double value)                              => Out.WriteLine(value);
	public static void WriteLine(float value)                               => Out.WriteLine(value);
	public static void WriteLine(int value)                                 => Out.WriteLine(value);
	public static void WriteLine(uint value)                                => Out.WriteLine(value);
	public static void WriteLine(long value)                                => Out.WriteLine(value);
	public static void WriteLine(ulong value)                               => Out.WriteLine(value);
	public static void WriteLine(object? value)                             => Out.WriteLine(value);
	public static void WriteLine(string? value)                             => Out.WriteLine(value);
	public static void WriteLine(string format, object? arg0)                            => Out.WriteLine(format, arg0);
	public static void WriteLine(string format, object? arg0, object? arg1)              => Out.WriteLine(format, arg0, arg1);
	public static void WriteLine(string format, object? arg0, object? arg1, object? arg2) => Out.WriteLine(format, arg0, arg1, arg2);
	public static void WriteLine(string format, params object?[] arg)                    => Out.WriteLine(format, arg);

	// ── Read ────────────────────────────────────────────────────────────

	public static int Read() => In.Read();
	public static string? ReadLine() => In.ReadLine();

	public static ConsoleKeyInfo ReadKey() => ReadKey(false);
	public static ConsoleKeyInfo ReadKey(bool intercept)
	{
		if (_channel == null)
			return System.Console.ReadKey(intercept);

		var data = RequestResponse(Op.ReadKey, new[] { intercept ? (byte)1 : (byte)0 });
		if (data.Length >= 4)
		{
			var keyChar = (char)BitConverter.ToUInt16(data, 0);
			var key     = (ConsoleKey)data[2];
			var mods    = (ConsoleModifiers)data[3];
			return new ConsoleKeyInfo(keyChar, key, (mods & ConsoleModifiers.Shift) != 0,
				(mods & ConsoleModifiers.Alt) != 0, (mods & ConsoleModifiers.Control) != 0);
		}
		return default;
	}

	public static bool KeyAvailable
	{
		get => _channel != null ? RequestBool(Op.GetKeyAvailable) : System.Console.KeyAvailable;
	}

	// ── Properties ──────────────────────────────────────────────────────

	public static string Title
	{
		get => _channel != null ? RequestString(Op.GetTitle)
			: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? System.Console.Title : "";
		set { if (_channel != null) Fire(Op.SetTitle, Encoding.UTF8.GetBytes(value)); else System.Console.Title = value; }
	}

	public static ConsoleColor ForegroundColor
	{
		get => _channel != null ? (ConsoleColor)RequestByte(Op.GetForeground)    : System.Console.ForegroundColor;
		set { if (_channel != null) Fire(Op.SetForeground, new[] { (byte)value }); else System.Console.ForegroundColor = value; }
	}

	public static ConsoleColor BackgroundColor
	{
		get => _channel != null ? (ConsoleColor)RequestByte(Op.GetBackground)    : System.Console.BackgroundColor;
		set { if (_channel != null) Fire(Op.SetBackground, new[] { (byte)value }); else System.Console.BackgroundColor = value; }
	}

	public static void ResetColor()
	{
		if (_channel != null) Fire(Op.ResetColor, Array.Empty<byte>());
		else System.Console.ResetColor();
	}

	public static int CursorLeft
	{
		get => _channel != null ? RequestInt(Op.GetCursorLeft) : System.Console.CursorLeft;
		set => SetCursorPosition(value, CursorTop);
	}

	public static int CursorTop
	{
		get => _channel != null ? RequestInt(Op.GetCursorTop) : System.Console.CursorTop;
		set => SetCursorPosition(CursorLeft, value);
	}

	public static bool CursorVisible
	{
		get => _channel != null ? RequestBool(Op.GetCursorVisible) :
			RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && System.Console.CursorVisible;
		set { if (_channel != null) Fire(Op.SetCursorVisible, new[] { value ? (byte)1 : (byte)0 }); else System.Console.CursorVisible = value; }
	}

	public static void SetCursorPosition(int left, int top)
	{
		if (_channel != null)
		{
			var data = new byte[8];
			BitConverter.TryWriteBytes(data.AsSpan(0, 4), left);
			BitConverter.TryWriteBytes(data.AsSpan(4, 4), top);
			Fire(Op.SetCursorPosition, data);
		}
		else
		{
			System.Console.SetCursorPosition(left, top);
		}
	}

	public static int BufferWidth  => _channel != null ? RequestInt(Op.GetBufferWidth)  : System.Console.BufferWidth;
	public static int BufferHeight => _channel != null ? RequestInt(Op.GetBufferHeight) : System.Console.BufferHeight;
	public static int WindowWidth  => _channel != null ? RequestInt(Op.GetWindowWidth)  : System.Console.WindowWidth;
	public static int WindowHeight => _channel != null ? RequestInt(Op.GetWindowHeight) : System.Console.WindowHeight;

	public static void Clear()
	{
		if (_channel != null) Fire(Op.Clear, Array.Empty<byte>());
		else System.Console.Clear();
	}

	public static void Beep()
	{
		if (_channel != null) Fire(Op.Beep, Array.Empty<byte>());
		else System.Console.Beep();
	}

	// ── Encoding ────────────────────────────────────────────────────────

	public static Encoding InputEncoding
	{
		get => System.Console.InputEncoding;
		set => System.Console.InputEncoding = value;
	}

	public static Encoding OutputEncoding
	{
		get => System.Console.OutputEncoding;
		set => System.Console.OutputEncoding = value;
	}

	// ── Redirection detection ───────────────────────────────────────────

	public static bool IsInputRedirected  => _channel != null || System.Console.IsInputRedirected;
	public static bool IsOutputRedirected => _channel != null || System.Console.IsOutputRedirected;
	public static bool IsErrorRedirected  => _channel != null || System.Console.IsErrorRedirected;

	// ── Private helpers ─────────────────────────────────────────────────

	/// <summary>Fire-and-forget: send a ConsoleControl op with no response expected.</summary>
	private static void Fire(byte op, byte[] data)
	{
		try { _channel!.SendAsync(new ConsoleControl(op, data)).AsTask().GetAwaiter().GetResult(); }
		catch { }
	}

	/// <summary>Send a ConsoleControl op and block until the CLI responds.</summary>
	private static byte[] RequestResponse(byte op, byte[] data)
	{
		_requestLock.Wait();
		try
		{
			_responseData = null;
			try { _channel!.SendAsync(new ConsoleControl(op, data)).AsTask().GetAwaiter().GetResult(); }
			catch { return Array.Empty<byte>(); }

			// Block until ReceiveLoop delivers the response
			_responseReady.Wait(5000); // 5s timeout — avoid permanent deadlock
			return _responseData ?? Array.Empty<byte>();
		}
		finally
		{
			_requestLock.Release();
		}
	}

	private static int RequestInt(byte op)
	{
		var data = RequestResponse(op, Array.Empty<byte>());
		return data.Length >= 4 ? BitConverter.ToInt32(data, 0) : 0;
	}

	private static byte RequestByte(byte op)
	{
		var data = RequestResponse(op, Array.Empty<byte>());
		return data.Length > 0 ? data[0] : (byte)0;
	}

	private static bool RequestBool(byte op) => RequestByte(op) != 0;

	private static string RequestString(byte op)
	{
		var data = RequestResponse(op, Array.Empty<byte>());
		return Encoding.UTF8.GetString(data);
	}

	// ── Relay TextWriter ────────────────────────────────────────────────

	private sealed class RelayTextWriter : TextWriter
	{
		private readonly MessageChannel _channel;
		private readonly byte _stream;

		public override Encoding Encoding => Encoding.UTF8;

		public RelayTextWriter(MessageChannel channel, byte stream)
		{
			_channel = channel;
			_stream  = stream;
		}

		public override void Write(char value) => Write(new string(value, 1));
		public override void Write(string? value) { if (value != null) WriteBytes(Encoding.GetBytes(value)); }
		public override void Write(char[]? buffer) { if (buffer != null) WriteBytes(Encoding.GetBytes(buffer)); }
		public override void Write(char[] buffer, int index, int count) => WriteBytes(Encoding.GetBytes(buffer, index, count));
		public override void Write(ReadOnlySpan<char> buffer) => WriteBytes(Encoding.GetBytes(buffer.ToArray()));
		public override void WriteLine(string? value) => Write((value ?? "") + new string(CoreNewLine));
		public override void Flush() { }

		private void WriteBytes(byte[] data)
		{
			try { _channel.SendAsync(new ConsoleData(_stream, data)).AsTask().GetAwaiter().GetResult(); }
			catch { }
		}
	}

	// ── Relay TextReader ────────────────────────────────────────────────

	private sealed class RelayTextReader : TextReader
	{
		private readonly MessageChannel _channel;
		private readonly ConcurrentQueue<string> _lines = new();
		private readonly SemaphoreSlim _lineReady = new(0);

		public RelayTextReader(MessageChannel channel) { _channel = channel; }

		public override string? ReadLine()
		{
			// Send stdin request (empty data = "I need a line")
			try { _channel.SendAsync(new ConsoleData(2, Array.Empty<byte>())).AsTask().GetAwaiter().GetResult(); }
			catch { return null; }

			// Block until ReceiveLoop delivers a line
			if (!_lineReady.Wait(30_000)) return null; // 30s timeout
			return _lines.TryDequeue(out var line) ? line : null;
		}

		public override int Read()
		{
			var line = ReadLine();
			return line != null && line.Length > 0 ? line[0] : -1;
		}

		/// <summary>Called by Sea.ReceiveLoop when a ConsoleData(stdin) response arrives.</summary>
		internal void DeliverLine(byte[] data)
		{
			_lines.Enqueue(Encoding.UTF8.GetString(data));
			_lineReady.Release();
		}
	}
}
