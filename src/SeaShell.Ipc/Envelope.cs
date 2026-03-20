using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeaShell.Ipc;

/// <summary>
/// Wire envelope: a type discriminator + JSON payload.
/// Keeps the protocol extensible without changing framing.
/// </summary>
public sealed record Envelope(string Type, JsonElement Payload)
{
	private static readonly JsonSerializerOptions JsonOpts = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public static Envelope Wrap<T>(T message) where T : notnull
	{
		var element = JsonSerializer.SerializeToElement(message, JsonOpts);
		return new Envelope(typeof(T).Name, element);
	}

	public T Unwrap<T>() => Payload.Deserialize<T>(JsonOpts)
		?? throw new InvalidOperationException($"Failed to deserialize {Type} as {typeof(T).Name}");

	public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOpts);

	public static Envelope FromBytes(byte[] bytes) =>
		JsonSerializer.Deserialize<Envelope>(bytes, JsonOpts)
		?? throw new InvalidOperationException("Failed to deserialize envelope");

	public static Envelope FromBytes(ReadOnlySpan<byte> bytes) =>
		JsonSerializer.Deserialize<Envelope>(bytes, JsonOpts)
		?? throw new InvalidOperationException("Failed to deserialize envelope");
}
