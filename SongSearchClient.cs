using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using MelonLoader;

namespace MaimaiLiveRequestMod
{
    internal sealed class GameMusicCatalog
    {
        private readonly bool _debug;
        private readonly string _modDirectory;
        private readonly object _gate = new object();
        private List<MusicInfo> _catalog = new List<MusicInfo>();
        private Dictionary<int, MusicInfo> _xmlCatalog;
        private string _lastError = "";
        private string _lastParser = "";
        private string _lastScanStats = "";

        public GameMusicCatalog(string modDirectory, bool debug)
        {
            _modDirectory = modDirectory;
            _debug = debug;
        }

        public List<MusicInfo> GetCatalog()
        {
            lock (_gate)
            {
                return new List<MusicInfo>(_catalog);
            }
        }

        public int CatalogCount
        {
            get
            {
                lock (_gate)
                {
                    return _catalog.Count;
                }
            }
        }

        public string LastError
        {
            get { return _lastError; }
        }

        public string LastParser
        {
            get { return _lastParser; }
        }

        public string LastScanStats
        {
            get { return _lastScanStats; }
        }

        public void RefreshCatalog()
        {
            _lastParser = "DataManager.GetMusics";
            try
            {
                var dataManagerType = AccessTools.TypeByName("Manager.DataManager");
                if (dataManagerType == null)
                {
                    throw new InvalidOperationException("Manager.DataManager not found");
                }

                var instanceProperty = dataManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (instanceProperty == null)
                {
                    throw new InvalidOperationException("DataManager.Instance not found");
                }

                var instance = instanceProperty.GetValue(null, null);
                if (instance == null)
                {
                    throw new InvalidOperationException("DataManager.Instance is null");
                }

                var getMusics = dataManagerType.GetMethod("GetMusics", BindingFlags.Public | BindingFlags.Instance);
                if (getMusics == null)
                {
                    throw new InvalidOperationException("DataManager.GetMusics not found");
                }

                var musics = getMusics.Invoke(instance, null) as IEnumerable;
                if (musics == null)
                {
                    throw new InvalidOperationException("DataManager.GetMusics returned null");
                }

                var list = new List<MusicInfo>();
                foreach (var item in musics)
                {
                    var value = item.GetType().GetProperty("Value").GetValue(item, null);
                    if (value == null || IsDisabled(value))
                    {
                        continue;
                    }

                    var id = Convert.ToInt32(value.GetType().GetMethod("GetID").Invoke(value, null));
                    var name = StringId(value, "name");
                    if (id <= 0 || string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    list.Add(new MusicInfo(id, name, StringId(value, "artistName")));
                }

                var xmlCount = MergeXmlCatalog(list);

                lock (_gate)
                {
                    _catalog = list;
                }

                _lastError = list.Count == 0 ? "DataManager.GetMusics returned 0 songs" : "";
                _lastScanStats = "dataManager=" + list.Count + ",xml=" + xmlCount;
                MelonLogger.Msg("Loaded game music catalog: " + list.Count + " songs.");
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                if (_debug)
                {
                    MelonLogger.Warning("Failed to load game music catalog: " + ex);
                }
                else
                {
                    MelonLogger.Warning("Failed to load game music catalog: " + ex.Message);
                }
            }
        }

        public void RefreshFromMusicSelect(object combineMusicDataList)
        {
            _lastParser = "MusicSelectProcess._combineMusicDataList";
            try
            {
                var categories = combineMusicDataList as IEnumerable;
                if (categories == null)
                {
                    throw new InvalidOperationException("MusicSelect combine list is null");
                }

                var seen = new HashSet<int>();
                var list = new List<MusicInfo>();
                var categoryCount = 0;
                var combineCount = 0;
                var selectDataCount = 0;
                var musicDataCount = 0;
                var idCount = 0;
                var xmlHitCount = 0;
                var scoreKindType = AccessTools.TypeByName("MAI2System.ConstParameter+ScoreKind");
                var standard = scoreKindType == null ? null : Enum.ToObject(scoreKindType, 0);
                var deluxe = scoreKindType == null ? null : Enum.ToObject(scoreKindType, 1);
                var xmlCatalog = GetXmlCatalog();

                foreach (var category in categories)
                {
                    categoryCount++;
                    var songs = category as IEnumerable;
                    if (songs == null)
                    {
                        continue;
                    }

                    foreach (var song in songs)
                    {
                        combineCount++;
                        AddFromId(song, standard, seen, list, xmlCatalog, ref idCount, ref xmlHitCount);
                        AddFromId(song, deluxe, seen, list, xmlCatalog, ref idCount, ref xmlHitCount);

                        var musicSelectData = Field(song, "musicSelectData") as IEnumerable;
                        if (musicSelectData == null)
                        {
                            continue;
                        }

                        foreach (var row in musicSelectData)
                        {
                            selectDataCount++;
                            var musicData = Field(row, "MusicData");
                            if (musicData == null || IsDisabled(musicData))
                            {
                                continue;
                            }

                            musicDataCount++;
                            var id = Convert.ToInt32(musicData.GetType().GetMethod("GetID").Invoke(musicData, null));
                            var name = StringId(musicData, "name");
                            if (id <= 0 || string.IsNullOrEmpty(name) || !seen.Add(id))
                            {
                                continue;
                            }

                            list.Add(new MusicInfo(id, name, StringId(musicData, "artistName")));
                        }
                    }
                }

                _lastScanStats = "categories=" + categoryCount + ",combine=" + combineCount + ",selectData=" + selectDataCount + ",musicData=" + musicDataCount + ",ids=" + idCount + ",xml=" + xmlHitCount;
                lock (_gate)
                {
                    _catalog = list;
                }

                _lastError = list.Count == 0 ? "MusicSelectProcess._combineMusicDataList returned 0 songs; " + _lastScanStats : "";
                MelonLogger.Msg("Loaded music select catalog: " + list.Count + " songs.");
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                if (_debug)
                {
                    MelonLogger.Warning("Failed to load music select catalog: " + ex);
                }
                else
                {
                    MelonLogger.Warning("Failed to load music select catalog: " + ex.Message);
                }
            }
        }

        private static bool IsDisabled(object musicData)
        {
            var method = musicData.GetType().GetMethod("IsDisable", BindingFlags.Public | BindingFlags.Instance);
            return method != null && Convert.ToBoolean(method.Invoke(musicData, null));
        }

        private static void AddFromId(object combineMusicSelectData, object scoreKind, HashSet<int> seen, List<MusicInfo> list, Dictionary<int, MusicInfo> xmlCatalog, ref int idCount, ref int xmlHitCount)
        {
            var rawId = GetCombineId(combineMusicSelectData, scoreKind);
            if (rawId <= 0)
            {
                return;
            }

            idCount++;
            var id = rawId >= 10000 && rawId < 100000 ? rawId % 10000 : rawId;
            if (!seen.Add(id))
            {
                return;
            }

            MusicInfo info;
            if (xmlCatalog.TryGetValue(id, out info))
            {
                xmlHitCount++;
                list.Add(info);
            }
            else
            {
                list.Add(new MusicInfo(id, "#" + id, ""));
            }
        }

        private static int GetCombineId(object combineMusicSelectData, object scoreKind)
        {
            if (combineMusicSelectData == null || scoreKind == null)
            {
                return -1;
            }

            try
            {
                var method = combineMusicSelectData.GetType().GetMethod("GetID", BindingFlags.Public | BindingFlags.Instance);
                return method == null ? -1 : Convert.ToInt32(method.Invoke(combineMusicSelectData, new[] { scoreKind }));
            }
            catch
            {
                return -1;
            }
        }

        private Dictionary<int, MusicInfo> GetXmlCatalog()
        {
            if (_xmlCatalog != null)
            {
                return _xmlCatalog;
            }

            var map = new Dictionary<int, MusicInfo>();
            foreach (var root in CandidateStreamingAssetsRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var file in Directory.GetFiles(root, "Music.xml", SearchOption.AllDirectories))
                {
                    var info = ParseMusicXml(file);
                    if (info != null && !map.ContainsKey(info.Id))
                    {
                        map.Add(info.Id, info);
                    }
                }
            }

            _xmlCatalog = map;
            return _xmlCatalog;
        }

        private int MergeXmlCatalog(List<MusicInfo> list)
        {
            var xmlCatalog = GetXmlCatalog();
            var index = new Dictionary<int, int>();
            for (var i = 0; i < list.Count; i++)
            {
                var id = NormalizeMusicId(list[i].Id);
                if (!index.ContainsKey(id))
                {
                    index.Add(id, i);
                }
            }

            foreach (var item in xmlCatalog)
            {
                int i;
                if (index.TryGetValue(item.Key, out i))
                {
                    list[i] = item.Value;
                }
                else
                {
                    index.Add(item.Key, list.Count);
                    list.Add(item.Value);
                }
            }

            return xmlCatalog.Count;
        }

        private IEnumerable<string> CandidateStreamingAssetsRoots()
        {
            yield return Path.Combine(Environment.CurrentDirectory, "Sinmai_Data", "StreamingAssets");

            var dir = _modDirectory;
            for (var i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
            {
                yield return Path.Combine(dir, "Sinmai_Data", "StreamingAssets");
                yield return Path.Combine(Directory.GetParent(dir) == null ? dir : Directory.GetParent(dir).FullName, "Sinmai_Data", "StreamingAssets");
                dir = Directory.GetParent(dir) == null ? null : Directory.GetParent(dir).FullName;
            }
        }

        private static MusicInfo ParseMusicXml(string path)
        {
            try
            {
                var text = File.ReadAllText(path);
                var id = MatchInt(text, @"<dataName>\s*music(?<v>\d+)\s*</dataName>");
                if (id <= 0)
                {
                    id = MatchInt(Path.GetFileName(Path.GetDirectoryName(path)) ?? "", @"music(?<v>\d+)");
                }
                var name = MatchString(text, @"<name>.*?<str>(?<v>.*?)</str>.*?</name>");
                var artist = MatchString(text, @"<artistName>.*?<str>(?<v>.*?)</str>.*?</artistName>");
                id = NormalizeMusicId(id);
                return id <= 0 || string.IsNullOrEmpty(name) ? null : new MusicInfo(id, name, artist);
            }
            catch
            {
                return null;
            }
        }

        private static int NormalizeMusicId(int id)
        {
            return id >= 10000 && id < 100000 ? id % 10000 : id;
        }

        private static int MatchInt(string text, string pattern)
        {
            var match = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant);
            int value;
            return match.Success && int.TryParse(match.Groups["v"].Value, out value) ? value : 0;
        }

        private static string MatchString(string text, string pattern)
        {
            var match = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant);
            return match.Success ? WebUtility.HtmlDecode(match.Groups["v"].Value).Trim() : "";
        }

        private static string StringId(object owner, string propertyName)
        {
            if (owner == null)
            {
                return "";
            }

            var stringId = owner.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance).GetValue(owner, null);
            if (stringId == null)
            {
                return "";
            }

            var prop = stringId.GetType().GetProperty("str", BindingFlags.Public | BindingFlags.Instance);
            return prop == null ? "" : (prop.GetValue(stringId, null) as string ?? "");
        }

        private static object Field(object owner, string name)
        {
            if (owner == null)
            {
                return null;
            }

            var field = owner.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field == null ? null : field.GetValue(owner);
        }
    }

    internal sealed class MusicInfo
    {
        public readonly int Id;
        public readonly string Name;
        public readonly string Artist;

        public MusicInfo(int id, string name, string artist)
        {
            Id = id;
            Name = name ?? "";
            Artist = artist ?? "";
        }
    }

    internal sealed class SelectResult
    {
        public readonly bool Success;
        public readonly string Message;

        public SelectResult(bool success, string message)
        {
            Success = success;
            Message = message ?? "";
        }
    }
}
