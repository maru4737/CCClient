using System.Text.Json.Serialization;

namespace CCClient;

public sealed class WsClientEnvelope
{
    [JsonPropertyName("type")]
    public required string Type { get; init; } // "join" | "send" | "ping"

    [JsonPropertyName("roomId")]
    public string? RoomId { get; init; }

    [JsonPropertyName("user")]
    public string? User { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("senderId")]
    public string? SenderId { get; init; }

    [JsonPropertyName("afterSeq")]
    public long? AfterSeq { get; init; }
}

public sealed class WsServerEnvelope
{
    [JsonPropertyName("type")]
    public required string Type { get; init; } // "joined" | "message" | "pong" | "error" | "backlog"

    [JsonPropertyName("roomId")]
    public string? RoomId { get; init; }

    // message: payload = ChatMessage
    // backlog: payload = ChatMessage[]
    [JsonPropertyName("payload")]
    public object? Payload { get; init; }
}

public sealed class ChatMessage
{
    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("roomId")]
    public string RoomId { get; init; } = default!;

    [JsonPropertyName("user")]
    public string User { get; init; } = default!;

    [JsonPropertyName("text")]
    public string Text { get; init; } = default!;

    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; init; }

    [JsonPropertyName("senderId")]
    public string SenderId { get; init; } = default!;
}