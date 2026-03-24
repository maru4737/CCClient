using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CCClient;

Console.OutputEncoding = Encoding.UTF8;

// 고정 WS 주소
var url = "ws://116.39.236.154/ws";

// 대소문자 섞여도 파싱되게
var jsonOpt = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

// 콘솔 출력/입력 충돌 완화
var consoleLock = new object();
string inputBuffer = "";

// 채팅 출력 헬퍼: 입력 줄 지우고 메시지 출력 후 프롬프트 복구
void WriteChatLine(string line)
{
    lock (consoleLock)
    {
        var width = 120;
        try { width = Math.Max(Console.WindowWidth - 1, 1); } catch { /* ignore */ }

        Console.Write("\r" + new string(' ', width) + "\r");
        Console.WriteLine(line);
        Console.Write("> " + inputBuffer);
    }
}

Console.Write("방(RoomId): ");
var roomId = Console.ReadLine()?.Trim();
if (string.IsNullOrWhiteSpace(roomId))
{
    Console.WriteLine("RoomId가 필요합니다.");
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
Console.WriteLine($"[접속] room={opt.RoomId}, user={opt.User}");
Console.WriteLine("명령: /exit 종료, /ping 핑");
Console.WriteLine("> ");

await RunAsync(opt, afterSeq, seqStore, jsonOpt, cts.Token);

async Task RunAsync(ClientOptions opt, long afterSeq, LastSeqStore seqStore, JsonSerializerOptions jsonOpt, CancellationToken ct)
{
    using var ws = new ClientWebSocket();

    try
    {
        Console.WriteLine($"[연결시도] {opt.ServerWsUri}");

        await ws.ConnectAsync(opt.ServerWsUri, ct);

        Console.WriteLine($"[연결성공] State={ws.State}");
    }
    catch (WebSocketException ex)
    {
        Console.WriteLine("[WebSocketException]");
        Console.WriteLine($"Message: {ex.Message}");
        Console.WriteLine($"WebSocketErrorCode: {ex.WebSocketErrorCode}");
        Console.WriteLine(ex);
    }
    catch (Exception ex)
    {
        Console.WriteLine("[Exception]");
        Console.WriteLine(ex);
    }

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

                HandleServerMessage(json, opt, seqStore, jsonOpt);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            WriteChatLine($"[연결 종료] {ex.Message}");
        }
        catch (Exception ex)
        {
            WriteChatLine($"[오류] {ex.Message}");
        }
    }, ct);

    // 입력 루프
    try
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var line = Console.ReadLine();
            if (line is null) continue;

            inputBuffer = ""; // ReadLine 기반이라 실시간 입력 버퍼는 없음
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

            // 내 메시지도 채팅처럼 즉시 보여주고 싶으면 아래 주석 해제(서버 에코와 중복될 수 있음)
            // WriteChatLine($"(me) {line}");
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

void HandleServerMessage(string json, ClientOptions opt, LastSeqStore seqStore, JsonSerializerOptions jsonOpt)
{
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    var type = GetString(root, "type") ?? GetString(root, "Type");

    if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
    {
        var payload = GetProperty(root, "payload") ?? GetProperty(root, "Payload");
        WriteChatLine($"[서버 오류] {payload?.ToString() ?? ""}");
        return;
    }

    if (string.Equals(type, "joined", StringComparison.OrdinalIgnoreCase))
    {
        // join 메시지는 조용히 처리(원하면 출력)
        // var room = GetString(root, "roomId") ?? GetString(root, "RoomId");
        // WriteChatLine($"[joined] room={room}");
        return;
    }

    if (string.Equals(type, "pong", StringComparison.OrdinalIgnoreCase))
    {
        // 핑 응답도 조용히 처리(원하면 출력)
        // WriteChatLine("[pong]");
        return;
    }

    var payloadEl = GetProperty(root, "payload") ?? GetProperty(root, "Payload");
    if (payloadEl is null) return;

    if (string.Equals(type, "backlog", StringComparison.OrdinalIgnoreCase))
    {
        if (payloadEl.Value.ValueKind != JsonValueKind.Array) return;

        foreach (var item in payloadEl.Value.EnumerateArray())
        {
            ChatMessage? msg;
            try { msg = item.Deserialize<ChatMessage>(jsonOpt); }
            catch { continue; }

            if (msg is null) continue;

            PrintMsg(msg, opt);
            seqStore.Set(msg.RoomId, msg.Seq);
        }
        return;
    }

    if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
    {
        ChatMessage? msg;
        try { msg = payloadEl.Value.Deserialize<ChatMessage>(jsonOpt); }
        catch { return; }

        if (msg is null) return;

        PrintMsg(msg, opt);
        seqStore.Set(msg.RoomId, msg.Seq);
        return;
    }
}

JsonElement? GetProperty(JsonElement root, string name)
    => root.TryGetProperty(name, out var v) ? v : (JsonElement?)null;

string? GetString(JsonElement root, string name)
    => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

void PrintMsg(ChatMessage msg, ClientOptions opt)
{
    var mine = string.Equals(msg.SenderId, opt.SenderId, StringComparison.Ordinal);

    // 시간은 취향이지만 채팅처럼 보이게 짧게
    var time = msg.Time.ToLocalTime().ToString("HH:mm");

    if (mine)
        WriteChatLine($"[{time}] (me) {msg.Text}");
    else
        WriteChatLine($"[{time}] {msg.User}: {msg.Text}");
}

static async Task<bool> TrySendAsync(ClientWebSocket? ws, WsClientEnvelope env, CancellationToken ct)
{
    if (ws is null)
    {
        Console.WriteLine("[전송실패] WebSocket 인스턴스가 null 입니다.");
        return false;
    }

    try
    {
        if (ws.State != WebSocketState.Open)
        {
            Console.WriteLine($"[전송실패] WebSocket 상태가 Open이 아닙니다. State={ws.State}");
            return false;
        }

        var json = JsonSerializer.Serialize(env);
        var bytes = Encoding.UTF8.GetBytes(json);

        await ws.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: ct);

        return true;
    }
    catch (ObjectDisposedException)
    {
        Console.WriteLine("[전송실패] WebSocket이 이미 Dispose 되었습니다.");
        return false;
    }
    catch (WebSocketException ex)
    {
        Console.WriteLine($"[전송실패] WebSocketException: {ex.Message}");
        return false;
    }
}
static async Task SendAsync(ClientWebSocket ws, WsClientEnvelope env, CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(ws);
    ArgumentNullException.ThrowIfNull(env);

    // Dispose 된 객체는 State 접근/SendAsync 시 예외가 날 수 있으므로 예외 처리 포함
    try
    {
        if (ws.State != WebSocketState.Open)
        {
            throw new InvalidOperationException(
                $"WebSocket이 열린 상태가 아닙니다. CurrentState={ws.State}");
        }

        var json = JsonSerializer.Serialize(env);
        var bytes = Encoding.UTF8.GetBytes(json);

        await ws.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: ct);
    }
    catch (ObjectDisposedException ex)
    {
        throw new InvalidOperationException("WebSocket이 이미 Dispose 되어 전송할 수 없습니다.", ex);
    }
    catch (WebSocketException ex)
    {
        throw new InvalidOperationException("WebSocket 전송 중 오류가 발생했습니다.", ex);
    }
}

async Task<string?> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
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
            return null;
    }

    return Encoding.UTF8.GetString(ms.ToArray());
}