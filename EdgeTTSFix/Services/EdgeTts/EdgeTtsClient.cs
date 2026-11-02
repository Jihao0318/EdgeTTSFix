using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace EdgeTTSFix.Services.EdgeTts;

public sealed record EdgeTtsOptions(string Voice, string Rate, string Volume, string Pitch);

public sealed class EdgeTtsClient
{
    // Edge 155.0.4283.45 及以上的朗读功能使用 V2 令牌与新域名。
    private const string ChromiumVersion = "155.0.4283.45";
    private const string SecMsGecVersionV2 = "2-" + ChromiumVersion;
    private const string SecMsGecVersionV1 = "1-143.0.3650.75";
    private const string OutputFormat = "audio-24khz-48kbitrate-mono-mp3";

    private static readonly Uri SpeechEndpoint =
        new("wss://api.msedgeservices.com/tts/cognitiveservices/websocket/v1");

    private static readonly string UserAgent =
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        $"Chrome/{ChromiumVersion.Split('.')[0]}.0.0.0 Safari/537.36 Edg/{ChromiumVersion.Split('.')[0]}.0.0.0";

    private const string ConfigJson =
        "{\"context\":{\"synthesis\":{\"audio\":{" +
        "\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
        "\"outputFormat\":\"" + OutputFormat + "\"}}}}";

    private readonly ILogger _logger;

    public EdgeTtsClient(ILogger<EdgeTtsClient> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 把文本合成为 MP3 并写入目标路径。目标文件会先写入临时文件，完成后再原子替换。
    /// </summary>
    public async Task SynthesizeToFileAsync(
        string text,
        EdgeTtsOptions options,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var attempts = new[]
        {
            (UseV2Token: true, WindowOffsetMinutes: 0, Version: SecMsGecVersionV2, Name: "v2"),
            (UseV2Token: true, WindowOffsetMinutes: -5, Version: SecMsGecVersionV2, Name: "v2-prev-window"),
            (UseV2Token: false, WindowOffsetMinutes: 0, Version: SecMsGecVersionV1, Name: "v1-token"),
            (UseV2Token: false, WindowOffsetMinutes: -5, Version: SecMsGecVersionV1, Name: "v1-token-prev-window")
        };

        Exception? lastError = null;
        var lastKind = EdgeTtsErrorKind.Unknown;
        var timeout = GetAttemptTimeout(text);

        foreach (var attempt in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 连接失败通常是瞬时的，同一组合额外重试一次。
            for (var retry = 0; retry < 2; retry++)
            {
                var temporaryPath = string.Concat(
                    destinationPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
                try
                {
                    _logger.LogDebug(
                        "Edge TTS 尝试 {Attempt}（{Length} 字，超时 {Timeout:F0}s）",
                        attempt.Name, text.Length, timeout.TotalSeconds);

                    await SynthesizeOnceAsync(
                        SpeechEndpoint,
                        attempt.UseV2Token,
                        attempt.WindowOffsetMinutes,
                        attempt.Version,
                        text,
                        options,
                        temporaryPath,
                        cancellationToken);

                    File.Move(temporaryPath, destinationPath, true);
                    return;
                }
                catch (OperationCanceledException)
                {
                    TryDelete(temporaryPath);
                    throw;
                }
                catch (EdgeTtsException ex) when (ex.Kind == EdgeTtsErrorKind.NoAudio)
                {
                    TryDelete(temporaryPath);
                    lastError = ex;
                    lastKind = ex.Kind;
                    _logger.LogWarning(ex, "Edge TTS 尝试 {Attempt} 未返回音频，尝试下一个组合。", attempt.Name);
                    break;
                }
                catch (Exception ex) when (IsHandshakeRejection(ex))
                {
                    TryDelete(temporaryPath);
                    lastError = ex;
                    lastKind = EdgeTtsErrorKind.Handshake;
                    _logger.LogWarning(ex, "Edge TTS 尝试 {Attempt} 握手被拒绝，尝试下一个组合。", attempt.Name);
                    break;
                }
                catch (Exception ex) when (IsConnectionFailure(ex))
                {
                    TryDelete(temporaryPath);
                    lastError = ex;
                    lastKind = EdgeTtsErrorKind.Connection;
                    if (retry == 0)
                    {
                        _logger.LogWarning(ex, "Edge TTS 尝试 {Attempt} 连接失败，稍后重试。", attempt.Name);
                        await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken);
                        continue;
                    }

                    _logger.LogWarning(ex, "Edge TTS 尝试 {Attempt} 连接失败，尝试下一个组合。", attempt.Name);
                    break;
                }
                catch (EdgeTtsException ex)
                {
                    TryDelete(temporaryPath);
                    lastError = ex;
                    lastKind = ex.Kind;
                    _logger.LogWarning(ex, "Edge TTS 尝试 {Attempt} 失败（{Kind}）。", attempt.Name, ex.Kind);
                    throw;
                }
                catch (Exception ex)
                {
                    TryDelete(temporaryPath);
                    throw new EdgeTtsException(
                        $"Edge TTS 合成失败：{ex.Message}",
                        EdgeTtsErrorKind.Unknown,
                        ex);
                }
            }
        }

        throw new EdgeTtsException(
            $"Edge TTS 所有端点均失败：{lastError?.Message}",
            lastKind,
            lastError);
    }

    private async Task SynthesizeOnceAsync(
        Uri endpoint,
        bool useV2Token,
        int windowOffsetMinutes,
        string version,
        string text,
        EdgeTtsOptions options,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GetAttemptTimeout(text));
        var token = timeoutCts.Token;

        using var socket = new ClientWebSocket();
        ConfigureHeaders(socket);

        var now = DateTimeOffset.UtcNow.AddMinutes(windowOffsetMinutes);
        var secMsGec = useV2Token
            ? EdgeTtsTokenGenerator.GenerateV2(now)
            : EdgeTtsTokenGenerator.GenerateV1(now);

        var subscriptionParameter = endpoint.Host.Contains("msedgeservices", StringComparison.Ordinal)
            ? $"Ocp-Apim-Subscription-Key={EdgeTtsTokenGenerator.TrustedClientToken}"
            : $"TrustedClientToken={EdgeTtsTokenGenerator.TrustedClientToken}";

        var url = new Uri(string.Concat(
            endpoint,
            "?", subscriptionParameter,
            "&ConnectionId=", RandomHex(16, upperCase: false),
            "&Sec-MS-GEC=", secMsGec,
            "&Sec-MS-GEC-Version=", version));

        try
        {
            await socket.ConnectAsync(url, token);

            var requestId = Guid.NewGuid().ToString("N");
            await SendTextAsync(socket,
                $"X-RequestId:{requestId}\r\n" +
                "Content-Type:application/json; charset=utf-8\r\n" +
                $"Path:speech.config\r\n\r\n{ConfigJson}",
                token);

            await SendTextAsync(socket,
                $"X-RequestId:{requestId}\r\n" +
                "Content-Type:application/ssml+xml\r\n" +
                $"X-Timestamp:{Timestamp()}\r\n" +
                $"Path:ssml\r\n\r\n{BuildSsml(text, options)}",
                token);

            await ReceiveAudioAsync(socket, temporaryPath, token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EdgeTtsException("Edge TTS 请求超时。", EdgeTtsErrorKind.Timeout);
        }
    }

    private static void ConfigureHeaders(ClientWebSocket socket)
    {
        // 服务端只接受带 permessage-deflate 扩展的握手请求。
        socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions
        {
            ClientMaxWindowBits = 15,
            ServerMaxWindowBits = 15
        };

        socket.Options.SetRequestHeader("Pragma", "no-cache");
        socket.Options.SetRequestHeader("Cache-Control", "no-cache");
        socket.Options.SetRequestHeader("Accept", "*/*");
        socket.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        socket.Options.SetRequestHeader("User-Agent", UserAgent);
        socket.Options.SetRequestHeader("Cookie", $"muid={RandomHex(16, upperCase: true)};");
    }

    private static async Task ReceiveAudioAsync(ClientWebSocket socket, string temporaryPath, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var audio = new MemoryStream();

        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new EdgeTtsException(
                        $"服务端提前关闭连接：{socket.CloseStatus} {socket.CloseStatusDescription}",
                        EdgeTtsErrorKind.NoAudio);
                }

                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var data = message.ToArray();
            if (result.MessageType == WebSocketMessageType.Text)
            {
                if (TryReadPath(Encoding.UTF8.GetString(data)) == "turn.end")
                {
                    break;
                }

                continue;
            }

            if (data.Length < 2)
            {
                continue;
            }

            var headerLength = BinaryPrimitives.ReadUInt16BigEndian(data);
            if (data.Length < 2 + headerLength)
            {
                continue;
            }

            var headers = Encoding.ASCII.GetString(data, 2, headerLength);
            if (TryReadPath(headers) != "audio")
            {
                continue;
            }

            audio.Write(data, 2 + headerLength, data.Length - 2 - headerLength);
        }

        if (audio.Length == 0)
        {
            throw new EdgeTtsException("Edge TTS 未返回音频数据。", EdgeTtsErrorKind.NoAudio);
        }

        await File.WriteAllBytesAsync(temporaryPath, audio.ToArray(), token);
    }

    private static async Task SendTextAsync(ClientWebSocket socket, string message, CancellationToken token)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(message).AsMemory(), WebSocketMessageType.Text, true, token);
    }

    private static string? TryReadPath(string raw)
    {
        var separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headerBlock = separator >= 0 ? raw[..separator] : raw;

        foreach (var line in headerBlock.Split("\r\n"))
        {
            if (line.StartsWith("Path:", StringComparison.OrdinalIgnoreCase))
            {
                return line[5..].Trim();
            }
        }

        return null;
    }

    private static string BuildSsml(string text, EdgeTtsOptions options)
    {
        var voice = string.IsNullOrWhiteSpace(options.Voice) ? "zh-CN-XiaoxiaoNeural" : options.Voice.Trim();
        var locale = GetLocale(voice);
        var rate = NormalizePercent(options.Rate);
        var volume = NormalizePercent(options.Volume);
        var pitch = NormalizeHertz(options.Pitch);

        return "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\"" +
               $" xml:lang=\"{EscapeXml(locale)}\">" +
               $"<voice name=\"{EscapeXml(voice)}\">" +
               $"<prosody pitch=\"{pitch}\" rate=\"{rate}\" volume=\"{volume}\">" +
               $"{EscapeXml(text)}</prosody></voice></speak>";
    }

    private static string GetLocale(string voice)
    {
        var parts = voice.Split('-');
        return parts.Length >= 2 ? $"{parts[0]}-{parts[1]}" : "zh-CN";
    }

    private static string NormalizePercent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "+0%";
        }

        var trimmed = value.Trim();
        var numberPart = trimmed.EndsWith('%') ? trimmed[..^1] : trimmed;
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return "+0%";
        }

        var formatted = number.ToString("0.###", CultureInfo.InvariantCulture);
        return number >= 0 ? $"+{formatted}%" : $"{formatted}%";
    }

    private static string NormalizeHertz(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "+0Hz";
        }

        var trimmed = value.Trim();
        var numberPart = trimmed.EndsWith("Hz", StringComparison.OrdinalIgnoreCase) ? trimmed[..^2] : trimmed;
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return "+0Hz";
        }

        var formatted = number.ToString("0.###", CultureInfo.InvariantCulture);
        return number >= 0 ? $"+{formatted}Hz" : $"{formatted}Hz";
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

    private static string Timestamp() => DateTimeOffset.UtcNow.ToString(
        "ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
        CultureInfo.InvariantCulture);

    private static string RandomHex(int byteCount, bool upperCase)
    {
        var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount));
        return upperCase ? hex : hex.ToLowerInvariant();
    }

    private static bool IsHandshakeRejection(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is not WebSocketException)
            {
                continue;
            }

            if (current.Message.Contains("403", StringComparison.Ordinal) ||
                current.Message.Contains("401", StringComparison.Ordinal) ||
                current.Message.Contains("429", StringComparison.Ordinal) ||
                current.Message.Contains("500", StringComparison.Ordinal) ||
                current.Message.Contains("502", StringComparison.Ordinal) ||
                current.Message.Contains("503", StringComparison.Ordinal) ||
                current.Message.Contains("status code '200'", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsConnectionFailure(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is HttpRequestException or System.Net.Sockets.SocketException)
            {
                return true;
            }

            if (current is WebSocketException &&
                (current.Message.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("remote server", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("connection", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan GetAttemptTimeout(string text)
    {
        // 长文本需要更长的生成时间；短文本保持快速失败。
        var seconds = Math.Clamp(30 + text.Length * 0.01, 30, 300);
        return TimeSpan.FromSeconds(seconds);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件清理失败不影响后续流程。
        }
    }
}

public enum EdgeTtsErrorKind
{
    Unknown,
    Handshake,
    NoAudio,
    Timeout,
    Connection
}

public sealed class EdgeTtsException : Exception
{
    public EdgeTtsException(
        string message,
        EdgeTtsErrorKind kind = EdgeTtsErrorKind.Unknown,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public EdgeTtsErrorKind Kind { get; }
}
