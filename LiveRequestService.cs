using System;
using System.IO;
using System.Reflection;
using MelonLoader;

namespace MaimaiLiveRequestMod
{
    internal static class LiveRequestService
    {
        private static LiveRequestConfig _config;
        private static GameMusicCatalog _catalog;
        private static MusicSelector _selector;
        private static RequestQueue _queue;
        private static OverlayServer _overlay;
        private static BiliDanmakuClient _bili;
        private static BlivechatClient _blivechat;
        private static string _modDirectory;
        private static DateTime _autoSelectAtUtc = DateTime.MinValue;
        private static DateTime _catalogRetryAtUtc = DateTime.MinValue;
        private static object _lastMusicSelectProcess;
        private static object _lastCombineMusicDataList;

        public static void Start(LiveRequestConfig config)
        {
            Stop();

            _config = config;
            _modDirectory = GetModDirectory();
            _catalog = new GameMusicCatalog(_modDirectory, config.Debug);
            _catalog.RefreshCatalog();
            _selector = new MusicSelector();
            _queue = new RequestQueue(_catalog, Path.Combine(_modDirectory, "MaimaiSongAliases.txt"), Path.Combine(_modDirectory, "MaimaiSongBlacklist.txt"), config.MaxQueue, config.Debug);
            _overlay = new OverlayServer(config.OverlayPrefix, _queue, Path.Combine(_modDirectory, "MaimaiLiveRequestBoard.html"));
            _overlay.Start();

            if (config.DanmakuSource == "blivechat")
            {
                _blivechat = new BlivechatClient(config.BlivechatUrl, config.BlivechatRoomCode, config.BlivechatRoomId, config.Command, config.Debug, OnDanmakuRequest);
                _blivechat.Start();
            }
            else if (config.BiliRoomId > 0)
            {
                _bili = new BiliDanmakuClient(config.BiliRoomId, config.Command, config.Debug, OnDanmakuRequest);
                _bili.Start();
            }
            else
            {
                MelonLogger.Warning("BiliRoomId is 0. Danmaku connection is disabled; use /api/request for local testing.");
            }

            MelonLogger.Msg("Live request board: " + config.OverlayPrefix);
        }

        public static void Stop()
        {
            if (_bili != null)
            {
                _bili.Stop();
                _bili = null;
            }

            if (_blivechat != null)
            {
                _blivechat.Stop();
                _blivechat = null;
            }

            if (_overlay != null)
            {
                _overlay.Stop();
                _overlay = null;
            }

            _autoSelectAtUtc = DateTime.MinValue;
        }

        public static EnqueueResult Enqueue(string userId, string userName, string query)
        {
            var result = _queue == null
                ? EnqueueResult.Fail("service_not_ready")
                : _queue.Enqueue(userId, userName, query);
            if (result.Success)
            {
                ScheduleAutoSelect();
            }
            return result;
        }

        public static string HealthJson()
        {
            return "{"
                + "\"version\":\"0.2.30\","
                + "\"overlay\":\"ok\","
                + "\"catalogCount\":" + (_catalog == null ? 0 : _catalog.CatalogCount) + ","
                + "\"catalogParser\":" + Json(_catalog == null ? "" : _catalog.LastParser) + ","
                + "\"catalogScan\":" + Json(_catalog == null ? "" : _catalog.LastScanStats) + ","
                + "\"catalogError\":" + Json(_catalog == null ? "" : _catalog.LastError) + ","
                + "\"musicSelectReady\":" + (_selector != null && _selector.IsReady ? "true" : "false") + ","
                + "\"lastSelectError\":" + Json(_selector == null ? "" : _selector.LastError) + ","
                + "\"queueCount\":" + (_queue == null ? 0 : _queue.Count) + ","
                + "\"unresolvedCount\":" + (_queue == null ? 0 : _queue.UnresolvedCount) + ","
                + "\"autoSelect\":" + (_config != null && _config.AutoSelect ? "true" : "false") + ","
                + "\"danmakuSource\":" + Json(_config == null ? "" : _config.DanmakuSource) + ","
                + "\"biliEnabled\":" + (_config != null && _config.DanmakuSource == "bili" && _config.BiliRoomId > 0 ? "true" : "false") + ","
                + "\"blivechatEnabled\":" + (_config != null && _config.DanmakuSource == "blivechat" ? "true" : "false") + ","
                + "\"blivechatRoomConfigured\":" + (_config != null && !string.IsNullOrEmpty(_config.BlivechatRoomCode) ? "true" : "false") + ","
                + "\"blivechatRoomIdConfigured\":" + (_config != null && _config.BlivechatRoomId > 0 ? "true" : "false")
                + "}";
        }

        public static bool AutoSelectEnabled
        {
            get { return _config != null && _config.AutoSelect; }
        }

        public static void SetAutoSelect(bool enabled)
        {
            if (_config == null)
            {
                return;
            }

            _config.AutoSelect = enabled;
            if (enabled)
            {
                ScheduleAutoSelect();
            }
            else
            {
                _autoSelectAtUtc = DateTime.MinValue;
            }

            try
            {
                var entry = MelonPreferences.GetEntry<bool>("MaimaiLiveRequest", "AutoSelect");
                if (entry != null)
                {
                    entry.Value = enabled;
                    MelonPreferences.Save();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Failed to persist AutoSelect preference: " + ex.Message);
            }
        }

        public static string RefreshCatalogJson()
        {
            if (_catalog == null)
            {
                return "{\"success\":false,\"message\":\"service_not_ready\"}";
            }

            _catalog.RefreshCatalog();
            return HealthJson();
        }

        public static EnqueueResult SelectNextNow()
        {
            return TrySelectNext(false);
        }

        public static EnqueueResult ResolveRequest(int requestId, string query)
        {
            var result = _queue == null
                ? EnqueueResult.Fail("service_not_ready")
                : _queue.Resolve(requestId, query);
            if (result.Success)
            {
                ScheduleAutoSelect();
            }
            return result;
        }

        public static void CaptureMusicSelect(object process, object combineMusicDataList, object subSequenceArray, object currentPlayerSubSequence, object beforePlayerSubSequence)
        {
            if (_selector == null)
            {
                return;
            }

            _selector.Capture(process, combineMusicDataList, subSequenceArray, currentPlayerSubSequence, beforePlayerSubSequence);
            _lastMusicSelectProcess = process;
            _lastCombineMusicDataList = combineMusicDataList;
            RefreshCatalogFromMusicSelect();

            if (_config != null && _config.AutoSelect)
            {
                ScheduleAutoSelect();
            }
        }

        public static void OnUpdate()
        {
            if (_catalogRetryAtUtc != DateTime.MinValue && DateTime.UtcNow >= _catalogRetryAtUtc)
            {
                RefreshCatalogFromMusicSelect();
            }

            if (_autoSelectAtUtc == DateTime.MinValue || DateTime.UtcNow < _autoSelectAtUtc)
            {
                return;
            }

            _autoSelectAtUtc = DateTime.MinValue;
            TrySelectNext(true);
        }

        public static void ClearMusicSelect()
        {
            if (_selector != null)
            {
                _selector.Clear();
            }
            _lastMusicSelectProcess = null;
            _lastCombineMusicDataList = null;
            _autoSelectAtUtc = DateTime.MinValue;
        }

        private static void ScheduleAutoSelect()
        {
            if (_config != null && _config.AutoSelect && _selector != null && _selector.IsReady)
            {
                _autoSelectAtUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, _config.AutoSelectDelaySeconds * 1000));
            }
        }

        private static void RefreshCatalogFromMusicSelect()
        {
            if (_catalog == null || _catalog.CatalogCount > 0)
            {
                _catalogRetryAtUtc = DateTime.MinValue;
                return;
            }

            if (_lastMusicSelectProcess != null)
            {
                var field = _lastMusicSelectProcess.GetType().GetField("_combineMusicDataList", BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    _lastCombineMusicDataList = field.GetValue(_lastMusicSelectProcess);
                }
            }

            if (_lastCombineMusicDataList == null)
            {
                _catalogRetryAtUtc = DateTime.UtcNow.AddSeconds(1);
                return;
            }

            _catalog.RefreshFromMusicSelect(_lastCombineMusicDataList);
            _catalogRetryAtUtc = _catalog.CatalogCount > 0 ? DateTime.MinValue : DateTime.UtcNow.AddSeconds(1);
        }

        private static EnqueueResult TrySelectNext(bool requireAutoSelect)
        {
            if (_config == null || (requireAutoSelect && !_config.AutoSelect) || _queue == null || _selector == null)
            {
                return EnqueueResult.Fail("service_not_ready");
            }

            var selected = _queue.TryPopNextForSelection();
            if (selected == null)
            {
                return EnqueueResult.Fail("queue_empty");
            }

            var result = _selector.SelectMusic(selected.Music.Id);
            if (result.Success)
            {
                MelonLogger.Msg("Auto selected requested song: " + selected.Music.Name + " by " + selected.UserName);
                _queue.MarkSelected(selected);
                return EnqueueResult.Ok(selected);
            }

            _queue.ReturnToFront(selected);
            MelonLogger.Warning("Auto select failed: " + result.Message);
            return EnqueueResult.Fail(result.Message);
        }

        private static void OnDanmakuRequest(DanmakuMessage message)
        {
            var result = Enqueue(message.UserId, message.UserName, message.Text);
            if (_config.Debug || !result.Success)
            {
                MelonLogger.Msg("Danmaku request: " + message.UserName + " -> " + message.Text + " = " + result.Message);
            }
        }

        private static string GetModDirectory()
        {
            var location = Assembly.GetExecutingAssembly().Location;
            return string.IsNullOrEmpty(location) ? Environment.CurrentDirectory : Path.GetDirectoryName(location);
        }

        private static string Json(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }

    internal sealed class LiveRequestConfig
    {
        public readonly string OverlayPrefix;
        public readonly string DanmakuSource;
        public readonly string BlivechatUrl;
        public readonly string BlivechatRoomCode;
        public readonly int BlivechatRoomId;
        public readonly int BiliRoomId;
        public readonly string Command;
        public bool AutoSelect;
        public readonly double AutoSelectDelaySeconds;
        public readonly int MaxQueue;
        public readonly bool Debug;

        public LiveRequestConfig(string overlayPrefix, string danmakuSource, string blivechatUrl, string blivechatRoomCode, int blivechatRoomId, int biliRoomId, string command, bool autoSelect, double autoSelectDelaySeconds, int maxQueue, bool debug)
        {
            OverlayPrefix = overlayPrefix;
            DanmakuSource = string.IsNullOrEmpty(danmakuSource) ? "bili" : danmakuSource.Trim().ToLowerInvariant();
            BlivechatUrl = string.IsNullOrEmpty(blivechatUrl) ? "ws://127.0.0.1:12450/api/chat" : blivechatUrl;
            BlivechatRoomCode = blivechatRoomCode == null ? "" : blivechatRoomCode.Trim();
            BlivechatRoomId = blivechatRoomId > 0 ? blivechatRoomId : biliRoomId;
            BiliRoomId = biliRoomId;
            Command = NormalizeCommand(command);
            AutoSelect = autoSelect;
            AutoSelectDelaySeconds = autoSelectDelaySeconds;
            MaxQueue = maxQueue <= 0 ? 30 : maxQueue;
            Debug = debug;
        }

        private static string NormalizeCommand(string command)
        {
            var value = string.IsNullOrWhiteSpace(command) ? "" : command.Trim();
            if (string.IsNullOrEmpty(value) || value.IndexOf('\uFFFD') >= 0)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    MelonLogger.Warning("Command is garbled; fallback to default. Use Command = \"\\u70b9\\u6b4c\" in MelonPreferences.cfg.");
                }
                return "\u70b9\u6b4c";
            }
            return value;
        }
    }
}
