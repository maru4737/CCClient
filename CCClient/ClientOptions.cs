namespace CCClient;

public sealed class ClientOptions
{
    // 서버 ws 주소 (예: ws://localhost:5000/ws)
    public required Uri ServerWsUri { get; init; }

    public required string RoomId { get; init; }
    public required string User { get; init; }

    // 클라이언트 인스턴스 고유값(내가 보낸 메시지 식별)
    public string SenderId { get; init; } = Guid.NewGuid().ToString("N");
}