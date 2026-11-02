using System.Security.Cryptography;
using System.Text;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.SpeechService;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared.Helpers;
using EdgeTTSFix.Models;
using EdgeTTSFix.Services.EdgeTts;
using EdgeTTSFix.Shared;
using Microsoft.Extensions.Logging;

namespace EdgeTTSFix.Services.SpeechService;

[SpeechProviderInfo("classisland.speech.edgettsfix", "Edge TTS Fix")]
public class EdgeTtsFixSpeechService : ISpeechService
{
    public static string CacheFolderPath =>
        Path.Combine(GlobalConstants.PluginConfigFolder, "EdgeTtsCache");

    private static readonly char[] InvalidFolderChars = Path.GetInvalidFileNameChars();

    private readonly ILogger<EdgeTtsFixSpeechService> _logger;
    private readonly IAudioService _audioService;
    private readonly EdgeTtsClient _client;
    private readonly Queue<EdgeTtsPlayInfo> _playingQueue = new();
    private readonly object _queueLock = new();

    private EdgeTtsFixSettings _settings = new();
    private bool _isPlaying;
    private EdgeTtsPlayInfo? _currentPlayInfo;

    public EdgeTtsFixSpeechService(
        ILogger<EdgeTtsFixSpeechService> logger,
        IAudioService audioService,
        EdgeTtsClient client)
    {
        _logger = logger;
        _audioService = audioService;
        _client = client;
        ReloadConfig();
        _logger.LogInformation("初始化了 Edge TTS Fix 服务。");
    }

    public void ReloadConfig()
    {
        _settings = ConfigureFileHelper.LoadConfig<EdgeTtsFixSettings>(
            Path.Combine(GlobalConstants.PluginConfigFolder, "Settings.json"));
        EdgeTtsFixSettings.Normalize(_settings);
    }

    public void EnqueueSpeechQueue(string text)
    {
        ReloadConfig();

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var settings = _settings;
        var cachePath = GetCachePath(text, settings);
        _logger.LogInformation("使用音色 {Voice} 朗读文本：{Text}", settings.Voice, text);
        _logger.LogDebug("语音缓存路径：{CachePath}", cachePath);

        var playInfo = new EdgeTtsPlayInfo(cachePath, new CancellationTokenSource());
        if (!File.Exists(cachePath))
        {
            // 合成任务独立于播放取消：即使队列被清空，也让它完成并写入缓存。
            playInfo.DownloadTask = GenerateSpeechAsync(text, settings, cachePath);
        }

        lock (_queueLock)
        {
            _playingQueue.Enqueue(playInfo);
        }

        _ = ProcessPlayerListAsync();
    }

    public void ClearSpeechQueue()
    {
        try
        {
            _currentPlayInfo?.CancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 当前语音刚好播放结束并释放了取消源。
        }

        lock (_queueLock)
        {
            while (_playingQueue.Count > 0)
            {
                var playInfo = _playingQueue.Dequeue();
                playInfo.CancellationTokenSource.Cancel();
                playInfo.CancellationTokenSource.Dispose();
            }
        }
    }

    private async Task ProcessPlayerListAsync()
    {
        lock (_queueLock)
        {
            if (_isPlaying)
            {
                return;
            }

            _isPlaying = true;
        }

        try
        {
            while (true)
            {
                EdgeTtsPlayInfo? playInfo;
                lock (_queueLock)
                {
                    playInfo = _playingQueue.Count > 0 ? _playingQueue.Dequeue() : null;
                }

                if (playInfo == null)
                {
                    break;
                }

                _currentPlayInfo = playInfo;
                try
                {
                    if (playInfo.CancellationTokenSource.IsCancellationRequested)
                    {
                        continue;
                    }

                    if (playInfo.DownloadTask != null)
                    {
                        _logger.LogDebug("等待 Edge TTS 语音生成完成...");
                        var generated = await playInfo.DownloadTask;
                        if (!generated)
                        {
                            if (!playInfo.CancellationTokenSource.IsCancellationRequested)
                            {
                                _logger.LogError("语音 {FilePath} 生成失败。", playInfo.FilePath);
                            }

                            continue;
                        }
                    }

                    // 生成期间可能已经被 ClearSpeechQueue 取消播放。
                    if (playInfo.CancellationTokenSource.IsCancellationRequested)
                    {
                        continue;
                    }

                    if (!File.Exists(playInfo.FilePath))
                    {
                        _logger.LogError("语音文件不存在：{FilePath}", playInfo.FilePath);
                        continue;
                    }

                    _logger.LogDebug("开始播放 {FilePath}", playInfo.FilePath);
                    await using var audioStream = File.OpenRead(playInfo.FilePath);
                    await _audioService.PlayAudioAsync(
                        audioStream,
                        (float)ISpeechService.GlobalSettings.SpeechVolume,
                        playInfo.CancellationTokenSource.Token);
                    _logger.LogDebug("结束播放 {FilePath}", playInfo.FilePath);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("语音播放已取消：{FilePath}", playInfo.FilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "播放语音失败：{FilePath}", playInfo.FilePath);
                }
                finally
                {
                    _currentPlayInfo = null;
                    playInfo.CancellationTokenSource.Dispose();
                }
            }
        }
        finally
        {
            bool hasPending;
            lock (_queueLock)
            {
                _isPlaying = false;
                hasPending = _playingQueue.Count > 0;
            }

            if (hasPending)
            {
                _ = ProcessPlayerListAsync();
            }
        }
    }

    private async Task<bool> GenerateSpeechAsync(
        string text,
        EdgeTtsFixSettings settings,
        string filePath)
    {
        try
        {
            Directory.CreateDirectory(CacheFolderPath);
            var options = new EdgeTtsOptions(settings.Voice, settings.Rate, settings.Volume, settings.Pitch);
            await _client.SynthesizeToFileAsync(text, options, filePath, CancellationToken.None);
            _logger.LogDebug("Edge TTS 语音已保存到：{FilePath}", filePath);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Edge TTS 请求已取消。");
            return false;
        }
        catch (EdgeTtsException ex)
        {
            _logger.LogError(
                ex,
                "Edge TTS 语音生成失败（{Kind}），文本长度 {Length}。",
                ex.Kind,
                text.Length);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Edge TTS 语音生成失败。");
            return false;
        }
    }

    private const int ReadableNameLength = 24;

    private static string GetCachePath(string text, EdgeTtsFixSettings settings)
    {
        var key = string.Join('|', settings.Voice, settings.Rate, settings.Volume, settings.Pitch, text);
        var fullHash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var voiceFolder = SanitizeFolderName(settings.Voice);
        var directory = Path.Combine(CacheFolderPath, voiceFolder);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{BuildReadableFileName(text)}_{fullHash[..10]}.mp3");

        // 兼容旧版纯 MD5 文件名：首次访问时改名，避免重新生成。
        var legacyPath = Path.Combine(directory, fullHash + ".mp3");
        if (!File.Exists(path) && File.Exists(legacyPath))
        {
            try
            {
                File.Move(legacyPath, path);
            }
            catch
            {
                // 迁移失败时按新路径重新生成。
            }
        }

        return path;
    }

    private static string BuildReadableFileName(string text)
    {
        var builder = new StringBuilder(ReadableNameLength);
        var lastWasSpace = false;

        foreach (var rune in text.EnumerateRunes())
        {
            if (builder.Length >= ReadableNameLength)
            {
                break;
            }

            if (Rune.IsControl(rune))
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            var value = rune.ToString();
            if (value.Length == 1 && InvalidFolderChars.Contains(value[0]))
            {
                continue;
            }

            if (builder.Length + value.Length > ReadableNameLength)
            {
                break;
            }

            builder.Append(value);
            lastWasSpace = false;
        }

        var name = builder.ToString().Trim().TrimEnd('.');
        return name.Length == 0 ? "speech" : name;
    }

    private static string SanitizeFolderName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(InvalidFolderChars.Contains(c) ? '_' : c);
        }

        var name = builder.ToString().Trim();
        return name.Length == 0 ? "default" : name;
    }
}

public class EdgeTtsPlayInfo
{
    public EdgeTtsPlayInfo(string filePath, CancellationTokenSource cancellationTokenSource)
    {
        FilePath = filePath;
        CancellationTokenSource = cancellationTokenSource;
    }

    public string FilePath { get; }

    public CancellationTokenSource CancellationTokenSource { get; }

    public Task<bool>? DownloadTask { get; set; }
}
