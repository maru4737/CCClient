using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CCClient;

Console.OutputEncoding = Encoding.UTF8;

var url = "wss://localhost:1502/ws";

Console.Write("방(roomId): ");
var roomId = Console.ReadLine()?.Trim();
if (string.IsNullOrWhiteSpace(roomId))
{
    Console.WriteLine("roomId가 필요합니다.");
    return;
}

Console.Write("이름(user): ");
var user = Console.ReadLine()?.Trim();
if (string.IsNullOrWhiteSpace(user))
    user = "unknown";

var opt = new ClientOptions
{
    ServerWsUri = new Uri(url),
    RoomId = roomId,
    User = user
};

var seqStore = new LastSeqStore(Path.Combine(AppContext.BaseDirectory, "lastseq.json"));
var afterSeq = seqStore.Get(opt.RoomId);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine();
Console.WriteLine($"[접속] {opt.ServerWsUri} / room={opt.RoomId}, user={opt.User}, afterSeq={afterSeq}");
Console.WriteLine("명령: /exit 종료, /ping 핑, 그냥 입력하면 메시지 전송");
Console.WriteLine();

await RunAsync(opt, afterSeq, seqStore, cts.Token);

static async Task RunAsync(ClientOptions opt, long afterSeq, LastSeqStore seqStore, CancellationToken ct)
{
    using var ws = new ClientWebSocket();

    // 연결
    await ws.ConnectAsync(opt.ServerWsUri, ct);

    // join 전송
    await SendAsync(ws, new WsClientEnvelope
    {
        Type = "join",
        RoomId = opt.RoomId,
        User = opt.User,
        SenderId = opt.SenderId,
        AfterSeq = afterSeq
    }, ct);

    // 수신 루프
    var receiveTask = Task.Run(async () =>
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var json = await ReceiveTextAsync(ws, ct);
                if (json is null) break;

                HandleServerMessage(json, opt, seqStore);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"[WS 오류] {ex.Message}");
        }
    }, ct);

    // 입력 루프
    try
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var line = Console.ReadLine();
            if (line is null) continue;

            line = line.TrimEnd();

            if (string.Equals(line, "/exit", StringComparison.OrdinalIgnoreCase))
                break;

            if (string.Equals(line, "/ping", StringComparison.OrdinalIgnoreCase))
            {
                await SendAsync(ws, new WsClientEnvelope { Type = "ping" }, ct);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            await SendAsync(ws, new WsClientEnvelope
            {
                Type = "send",
                Text = line
            }, ct);
        }
    }
    finally
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client exit", CancellationToken.None);
        }
        catch { /* ignore */ }

        try { await receiveTask; } catch { /* ignore */ }
    }
}

static void HandleServerMessage(string json, ClientOptions opt, LastSeqStore seqStore)
{
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
    var roomId = root.TryGetProperty("roomId", out var r) ? r.GetString() : null;

    if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
    {
        var payload = root.TryGetProperty("payload", out var p) ? p.ToString() : "";
        Console.WriteLine($"[서버 오류] {payload}");
        return;
    }

    if (string.Equals(type, "joined", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"[joined] room={roomId}");
        return;
    }

    if (string.Equals(type, "pong", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("[pong]");
        return;
    }

    if (!root.TryGetProperty("payload", out var payloadEl))
        return;

    if (string.Equals(type, "backlog", StringComparison.OrdinalIgnoreCase))
    {
        // payload: array of ChatMessage
        if (payloadEl.ValueKind != JsonValueKind.Array) return;

        foreach (var item in payloadEl.EnumerateArray())
        {
            var msg = item.Deserialize<ChatMessage>();
            if (msg is null) continue;

            PrintMsg(msg, opt);
            seqStore.Set(msg.RoomId, msg.Seq);
        }
        return;
    }

    if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
    {
        // payload: ChatMessage
        var msg = payloadEl.Deserialize<ChatMessage>();
        if (msg is null) return;

        PrintMsg(msg, opt);
        seqStore.Set(msg.RoomId, msg.Seq);
        return;
    }
}

static void PrintMsg(ChatMessage msg, ClientOptions opt)
{
    // 내가 보낸 메시지는 표시를 다르게(원하면 필터링 가능)
    var mine = string.Equals(msg.SenderId, opt.SenderId, StringComparison.Ordinal);

    var time = msg.Time.ToLocalTime().ToString("HH:mm:ss");
    if (mine)
        Console.WriteLine($"[{time}] (me) {msg.Text}  (seq={msg.Seq})");
    else
        Console.WriteLine($"[{time}] {msg.User}: {msg.Text}  (seq={msg.Seq})");
}

static async Task SendAsync(ClientWebSocket ws, WsClientEnvelope env, CancellationToken ct)
{
    var json = JsonSerializer.Serialize(env);
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
}

static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
{
    var buffer = new byte[8 * 1024];
    using var ms = new MemoryStream();

    while (true)
    {
        var result = await ws.ReceiveAsync(buffer, ct);
        if (result.MessageType == WebSocketMessageType.Close)
            return null;

        ms.Write(buffer, 0, result.Count);

        if (result.EndOfMessage)
            break;

        if (ms.Length > 256 * 1024)
            return null; // 안전장치
    }

    return Encoding.UTF8.GetString(ms.ToArray());
}