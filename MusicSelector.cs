using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace MaimaiLiveRequestMod
{
    internal sealed class MusicSelector
    {
        private object _process;
        private object _combineMusicDataList;
        private object _subSequenceArray;
        private object _currentPlayerSubSequence;
        private object _beforePlayerSubSequence;
        private string _lastError = "";

        public bool IsReady
        {
            get { return _process != null && _combineMusicDataList != null; }
        }

        public string LastError
        {
            get { return _lastError; }
        }

        public void Capture(object process, object combineMusicDataList, object subSequenceArray, object currentPlayerSubSequence, object beforePlayerSubSequence)
        {
            _process = process;
            _combineMusicDataList = combineMusicDataList;
            _subSequenceArray = subSequenceArray;
            _currentPlayerSubSequence = currentPlayerSubSequence;
            _beforePlayerSubSequence = beforePlayerSubSequence;
            _lastError = "";
        }

        public void Clear()
        {
            _process = null;
            _combineMusicDataList = null;
            _subSequenceArray = null;
            _currentPlayerSubSequence = null;
            _beforePlayerSubSequence = null;
        }

        public SelectResult SelectMusic(int id)
        {
            try
            {
                if (!IsReady)
                {
                    return Fail("music_select_not_ready");
                }

                var hit = FindMusicIndex(id);
                if (!hit.Found)
                {
                    return Fail("song_not_in_current_select_list:" + id);
                }

                SetProperty(_process, "CurrentCategorySelect", hit.CategoryIndex);
                SetProperty(_process, "CurrentMusicSelect", hit.MusicIndex);
                SetScoreType(hit.ScoreKind);
                Invoke(_process, "ChangeBGM");
                RefreshPlayerSubSequences();

                _lastError = "";
                return new SelectResult(true, "selected");
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        private SelectHit FindMusicIndex(int id)
        {
            var categories = _combineMusicDataList as IEnumerable;
            if (categories == null)
            {
                return SelectHit.Miss();
            }

            var scoreKindType = AccessTools.TypeByName("MAI2System.ConstParameter+ScoreKind");
            var standard = Enum.ToObject(scoreKindType, 0);
            var deluxe = Enum.ToObject(scoreKindType, 1);
            var categoryIndex = 0;
            foreach (var category in categories)
            {
                var songs = category as IEnumerable;
                if (songs == null)
                {
                    categoryIndex++;
                    continue;
                }

                var musicIndex = 0;
                foreach (var song in songs)
                {
                    if (GetId(song, standard) == id)
                    {
                        return SelectHit.Hit(categoryIndex, musicIndex, 0);
                    }

                    var deluxeId = GetId(song, deluxe);
                    if (deluxeId == id || (id < 10000 && deluxeId == id + 10000))
                    {
                        return SelectHit.Hit(categoryIndex, musicIndex, 1);
                    }

                    musicIndex++;
                }

                categoryIndex++;
            }

            return SelectHit.Miss();
        }

        private static int GetId(object combineMusicSelectData, object scoreKind)
        {
            try
            {
                var method = combineMusicSelectData.GetType().GetMethod("GetID", BindingFlags.Public | BindingFlags.Instance);
                return Convert.ToInt32(method.Invoke(combineMusicSelectData, new[] { scoreKind }));
            }
            catch
            {
                return -1;
            }
        }

        private void SetScoreType(int scoreKind)
        {
            var prop = _process.GetType().GetProperty("ScoreType", BindingFlags.Public | BindingFlags.Instance);
            var value = Enum.ToObject(prop.PropertyType, scoreKind);
            prop.SetValue(_process, value, null);
        }

        private void RefreshPlayerSubSequences()
        {
            var monitors = GetProperty(_process, "MonitorArray") as Array;
            if (monitors == null)
            {
                return;
            }

            for (var player = 0; player < monitors.Length; player++)
            {
                if (!IsEntry(player))
                {
                    continue;
                }

                var monitor = monitors.GetValue(player);
                Invoke(monitor, "SetScrollMusicCard", false);
                Invoke(monitor, "SetScrollGenreCard", false);
                Invoke(monitor, "OutGenreTab");
                SwitchToDifficultySequence(player);
            }
        }

        private bool IsEntry(int player)
        {
            var method = _process.GetType().GetMethod("IsEntry", BindingFlags.Public | BindingFlags.Instance);
            return method == null || Convert.ToBoolean(method.Invoke(_process, new object[] { player }));
        }

        private void SwitchToDifficultySequence(int player)
        {
            var subSequences = _subSequenceArray as Array;
            var current = _currentPlayerSubSequence as Array;
            var before = _beforePlayerSubSequence as Array;
            if (subSequences == null || current == null || before == null || player >= current.Length)
            {
                return;
            }

            var currentValue = Convert.ToInt32(current.GetValue(player));
            var playerSequences = subSequences.GetValue(player) as Array;
            if (playerSequences != null && currentValue >= 0 && currentValue < playerSequences.Length)
            {
                Invoke(playerSequences.GetValue(currentValue), "Reset");
            }

            before.SetValue(current.GetValue(player), player);
            current.SetValue(Enum.ToObject(current.GetType().GetElementType(), 3), player);

            if (playerSequences != null && playerSequences.Length > 3)
            {
                Invoke(playerSequences.GetValue(3), "OnStartSequence");
            }
        }

        private SelectResult Fail(string message)
        {
            _lastError = message ?? "select_failed";
            return new SelectResult(false, _lastError);
        }

        private static object GetProperty(object target, string name)
        {
            return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance).GetValue(target, null);
        }

        private static void SetProperty(object target, string name, object value)
        {
            target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance).SetValue(target, value, null);
        }

        private static void Invoke(object target, string name)
        {
            target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance).Invoke(target, null);
        }

        private static void Invoke(object target, string name, object arg)
        {
            target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance).Invoke(target, new[] { arg });
        }

        private struct SelectHit
        {
            public readonly bool Found;
            public readonly int CategoryIndex;
            public readonly int MusicIndex;
            public readonly int ScoreKind;

            private SelectHit(bool found, int categoryIndex, int musicIndex, int scoreKind)
            {
                Found = found;
                CategoryIndex = categoryIndex;
                MusicIndex = musicIndex;
                ScoreKind = scoreKind;
            }

            public static SelectHit Hit(int categoryIndex, int musicIndex, int scoreKind)
            {
                return new SelectHit(true, categoryIndex, musicIndex, scoreKind);
            }

            public static SelectHit Miss()
            {
                return new SelectHit(false, -1, -1, 0);
            }
        }
    }
}
