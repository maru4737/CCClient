namespace CCClient;

public sealed class ClientOptions
{
    public required Uri ServerWsUri { get; init; }

    public required string RoomId { get; init; }
    public required string User { get; init; }
    public string SenderId { get; init; } = Guid.NewGuid().ToString("N");
}