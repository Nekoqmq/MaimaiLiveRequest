using System;
using HarmonyLib;
using MelonLoader;

[assembly: MelonInfo(typeof(MaimaiLiveRequestMod.ModMain), "Maimai Live Request", "0.2.30", "Codex")]

namespace MaimaiLiveRequestMod
{
    public sealed class ModMain : MelonMod
    {
        private MelonPreferences_Entry<string> _overlayPrefix;
        private MelonPreferences_Entry<string> _danmakuSource;
        private MelonPreferences_Entry<string> _blivechatUrl;
        private MelonPreferences_Entry<string> _blivechatRoomCode;
        private MelonPreferences_Entry<int> _blivechatRoomId;
        private MelonPreferences_Entry<int> _biliRoomId;
        private MelonPreferences_Entry<string> _command;
        private MelonPreferences_Entry<bool> _autoSelect;
        private MelonPreferences_Entry<double> _autoSelectDelaySeconds;
        private MelonPreferences_Entry<int> _maxQueue;
        private MelonPreferences_Entry<bool> _debug;

        public override void OnInitializeMelon()
        {
            var category = MelonPreferences.CreateCategory("MaimaiLiveRequest");
            _overlayPrefix = category.CreateEntry("OverlayPrefix", "http://127.0.0.1:8890/");
            _danmakuSource = category.CreateEntry("DanmakuSource", "bili");
            _blivechatUrl = category.CreateEntry("BlivechatUrl", "ws://127.0.0.1:12450/api/chat");
            _blivechatRoomCode = category.CreateEntry("BlivechatRoomCode", "");
            _blivechatRoomId = category.CreateEntry("BlivechatRoomId", 0);
            _biliRoomId = category.CreateEntry("BiliRoomId", 0);
            _command = category.CreateEntry("Command", "\u70b9\u6b4c");
            _autoSelect = category.CreateEntry("AutoSelect", true);
            _autoSelectDelaySeconds = category.CreateEntry("AutoSelectDelaySeconds", 1.0);
            _maxQueue = category.CreateEntry("MaxQueue", 30);
            _debug = category.CreateEntry("Debug", false);

            LiveRequestService.Start(new LiveRequestConfig(
                _overlayPrefix.Value,
                _danmakuSource.Value,
                _blivechatUrl.Value,
                _blivechatRoomCode.Value,
                _blivechatRoomId.Value,
                _biliRoomId.Value,
                _command.Value,
                _autoSelect.Value,
                _autoSelectDelaySeconds.Value,
                _maxQueue.Value,
                _debug.Value));

            try
            {
                var harmony = new HarmonyLib.Harmony("local.maimai.live.request");
                var method = AccessTools.Method("Process.MusicSelectProcess:OnStart");
                if (method == null)
                {
                    MelonLogger.Warning("MusicSelectProcess.OnStart not found; auto select is disabled.");
                }
                else
                {
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(ModMain), nameof(OnMusicSelectStart)));
                    MelonLogger.Msg("MusicSelectProcess.OnStart hook applied.");
                }

                var release = AccessTools.Method("Process.MusicSelectProcess:OnRelease");
                if (release != null)
                {
                    harmony.Patch(release, postfix: new HarmonyMethod(typeof(ModMain), nameof(OnMusicSelectRelease)));
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Failed to patch MusicSelectProcess.OnStart: " + ex);
            }
        }

        public override void OnUpdate()
        {
            LiveRequestService.OnUpdate();
        }

        public override void OnDeinitializeMelon()
        {
            LiveRequestService.Stop();
        }

        private static void OnMusicSelectStart(
            object __instance,
            object ____combineMusicDataList,
            object ____subSequenceArray,
            object ____currentPlayerSubSequence,
            object ____beforePlayerSubSequence)
        {
            LiveRequestService.CaptureMusicSelect(
                __instance,
                ____combineMusicDataList,
                ____subSequenceArray,
                ____currentPlayerSubSequence,
                ____beforePlayerSubSequence);
        }

        private static void OnMusicSelectRelease()
        {
            LiveRequestService.ClearMusicSelect();
        }
    }
}
