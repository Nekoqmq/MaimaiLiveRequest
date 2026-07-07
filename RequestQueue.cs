using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MelonLoader;

namespace MaimaiLiveRequestMod
{
    internal sealed class RequestQueue
    {
        private readonly GameMusicCatalog _catalog;
        private readonly string _aliasPath;
        private readonly string _blacklistPath;
        private readonly int _maxQueue;
        private readonly bool _debug;
        private readonly object _gate = new object();
        private readonly List<SongRequest> _queue = new List<SongRequest>();
        private readonly List<SongRequest> _unresolved = new List<SongRequest>();
        private Dictionary<string, string> _aliases = new Dictionary<string, string>();
        private HashSet<int> _blacklist = new HashSet<int>();
        private int _nextRequestId = 1;
        private SongRequest _lastSelected;

        public RequestQueue(GameMusicCatalog catalog, string aliasPath, string blacklistPath, int maxQueue, bool debug)
        {
            _catalog = catalog;
            _aliasPath = aliasPath;
            _blacklistPath = blacklistPath;
            _maxQueue = maxQueue;
            _debug = debug;
            EnsureAliasFile();
            EnsureBlacklistFile();
            LoadAliases();
            LoadBlacklist();
        }

        public EnqueueResult Enqueue(string userId, string userName, string query)
        {
            LoadAliases();
            LoadBlacklist();
            userId = userId ?? "";
            userName = userName ?? "";
            query = (query ?? "").Trim();
            if (query.Length == 0)
            {
                return EnqueueResult.Fail("empty_query");
            }

            var isAdmin = IsAdmin(userId, userName);
            var basicError = CheckBasicEnqueue(userId, isAdmin);
            if (basicError.Length > 0)
            {
                return EnqueueResult.Fail(basicError);
            }

            var match = FindMusic(query);
            if (match == null)
            {
                _catalog.RefreshCatalog();
                match = FindMusic(query);
            }

            if (match == null)
            {
                return AddUnresolved(userId, userName, query, isAdmin);
            }

            if (IsBlacklisted(match.Id))
            {
                return EnqueueResult.Fail("song_blacklisted");
            }

            lock (_gate)
            {
                basicError = CheckBasicEnqueueLocked(userId, isAdmin);
                if (basicError.Length > 0)
                {
                    return EnqueueResult.Fail(basicError);
                }

                if (!isAdmin && _queue.Any(x => x.Music.Id == match.Id))
                {
                    return EnqueueResult.Fail("song_already_queued");
                }

                var request = new SongRequest(_nextRequestId++, userId, userName, query, match, DateTimeOffset.UtcNow);
                _queue.Add(request);
                if (_debug)
                {
                    MelonLogger.Msg("Queued song: " + request.Music.Name + " by " + userName);
                }
                return EnqueueResult.Ok(request);
            }
        }

        public EnqueueResult Resolve(int requestId, string query)
        {
            LoadAliases();
            LoadBlacklist();
            query = (query ?? "").Trim();
            if (query.Length == 0)
            {
                return EnqueueResult.Fail("empty_query");
            }

            var match = FindMusic(query);
            if (match == null)
            {
                _catalog.RefreshCatalog();
                match = FindMusic(query);
            }

            if (match == null)
            {
                return EnqueueResult.Fail("song_not_found");
            }

            if (IsBlacklisted(match.Id))
            {
                return EnqueueResult.Fail("song_blacklisted");
            }

            lock (_gate)
            {
                var index = _queue.FindIndex(x => x.RequestId == requestId);
                if (index >= 0)
                {
                    var old = _queue[index];
                    var request = new SongRequest(old.RequestId, old.UserId, old.UserName, query, match, old.CreatedAt);
                    _queue[index] = request;
                    return EnqueueResult.Ok(request);
                }

                index = _unresolved.FindIndex(x => x.RequestId == requestId);
                if (index < 0)
                {
                    return EnqueueResult.Fail("request_not_found");
                }

                var pending = _unresolved[index];
                _unresolved.RemoveAt(index);
                var resolved = new SongRequest(pending.RequestId, pending.UserId, pending.UserName, query, match, pending.CreatedAt);
                _queue.Add(resolved);
                return EnqueueResult.Ok(resolved);
            }
        }

        public EnqueueResult Remove(int requestId)
        {
            lock (_gate)
            {
                var index = _queue.FindIndex(x => x.RequestId == requestId);
                if (index >= 0)
                {
                    var removed = _queue[index];
                    _queue.RemoveAt(index);
                    return EnqueueResult.Ok(removed);
                }

                index = _unresolved.FindIndex(x => x.RequestId == requestId);
                if (index < 0)
                {
                    return EnqueueResult.Fail("request_not_found");
                }

                var pending = _unresolved[index];
                _unresolved.RemoveAt(index);
                return EnqueueResult.Ok(pending);
            }
        }

        public EnqueueResult Move(int requestId, int toIndex)
        {
            lock (_gate)
            {
                var index = _queue.FindIndex(x => x.RequestId == requestId);
                if (index < 0)
                {
                    return EnqueueResult.Fail("request_not_found");
                }

                if (_queue.Count == 0)
                {
                    return EnqueueResult.Fail("queue_empty");
                }

                toIndex = Math.Max(0, Math.Min(_queue.Count - 1, toIndex));
                var request = _queue[index];
                _queue.RemoveAt(index);
                _queue.Insert(toIndex, request);
                return EnqueueResult.Ok(request);
            }
        }

        public EnqueueResult MoveByDelta(int requestId, int delta)
        {
            lock (_gate)
            {
                var index = _queue.FindIndex(x => x.RequestId == requestId);
                if (index < 0)
                {
                    return EnqueueResult.Fail("request_not_found");
                }
                return Move(requestId, index + delta);
            }
        }

        public SongRequest TryPopNextForSelection()
        {
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    return null;
                }

                var request = _queue[0];
                _queue.RemoveAt(0);
                return request;
            }
        }

        public void ReturnToFront(SongRequest request)
        {
            if (request == null)
            {
                return;
            }

            lock (_gate)
            {
                _queue.Insert(0, request);
            }
        }

        public void MarkSelected(SongRequest request)
        {
            lock (_gate)
            {
                _lastSelected = request;
            }
        }

        public QueueSnapshot GetSnapshot()
        {
            lock (_gate)
            {
                return new QueueSnapshot(new List<SongRequest>(_queue), new List<SongRequest>(_unresolved), _lastSelected);
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _queue.Count;
                }
            }
        }

        public int UnresolvedCount
        {
            get
            {
                lock (_gate)
                {
                    return _unresolved.Count;
                }
            }
        }

        public void ReloadFiles()
        {
            LoadAliases();
            LoadBlacklist();
        }

        private EnqueueResult AddUnresolved(string userId, string userName, string query, bool isAdmin)
        {
            lock (_gate)
            {
                var basicError = CheckBasicEnqueueLocked(userId, isAdmin);
                if (basicError.Length > 0)
                {
                    return EnqueueResult.Fail(basicError);
                }

                var request = new SongRequest(_nextRequestId++, userId, userName, query, null, DateTimeOffset.UtcNow);
                _unresolved.Add(request);
                if (_debug)
                {
                    MelonLogger.Msg("Unresolved request: " + query + " by " + userName);
                }
                return EnqueueResult.Pending(request);
            }
        }

        private string CheckBasicEnqueue(string userId, bool isAdmin)
        {
            lock (_gate)
            {
                return CheckBasicEnqueueLocked(userId, isAdmin);
            }
        }

        private string CheckBasicEnqueueLocked(string userId, bool isAdmin)
        {
            if (isAdmin)
            {
                return "";
            }

            if (_queue.Count + _unresolved.Count >= _maxQueue)
            {
                return "queue_full";
            }

            if (_queue.Any(x => x.UserId == userId) || _unresolved.Any(x => x.UserId == userId))
            {
                return "user_already_queued";
            }

            return "";
        }

        private MusicInfo FindMusic(string rawQuery)
        {
            var query = ResolveAlias(rawQuery);
            int id;
            if (int.TryParse(query, out id))
            {
                id = NormalizeMusicId(id);
                var catalogById = _catalog.GetCatalog();
                if (catalogById.Count == 0)
                {
                    return new MusicInfo(id, "#" + id, "");
                }

                var byId = catalogById.FirstOrDefault(x => NormalizeMusicId(x.Id) == id);
                if (byId != null)
                {
                    return byId;
                }

                return new MusicInfo(id, "#" + id, "");
            }

            var catalog = _catalog.GetCatalog();
            if (catalog.Count == 0)
            {
                return null;
            }

            var nq = Normalize(query);
            if (nq.Length == 0)
            {
                return null;
            }

            var exact = catalog.FirstOrDefault(x => Normalize(x.Name) == nq);
            if (exact != null)
            {
                return exact;
            }

            if (nq.Length >= 2)
            {
                var contains = catalog
                    .Where(x => Normalize(x.Name).Contains(nq) || Normalize(x.Artist).Contains(nq))
                    .OrderBy(x => Normalize(x.Name).Length)
                    .FirstOrDefault();
                if (contains != null)
                {
                    return contains;
                }
            }

            if (nq.Length < 4)
            {
                return null;
            }

            var maxDistance = Math.Max(1, nq.Length / 4);
            var ranked = catalog
                .Select(x => new { Song = x, Score = Distance(nq, Normalize(x.Name)) })
                .Where(x => x.Score <= maxDistance)
                .OrderBy(x => x.Score)
                .ThenBy(x => x.Song.Name.Length)
                .Take(2)
                .ToList();
            if (ranked.Count == 0)
            {
                return null;
            }

            if (ranked.Count > 1 && ranked[0].Score == ranked[1].Score)
            {
                return null;
            }

            return ranked[0].Song;
        }

        private string ResolveAlias(string query)
        {
            string value;
            return _aliases.TryGetValue(Normalize(query), out value) ? value : query;
        }

        private void EnsureAliasFile()
        {
            if (File.Exists(_aliasPath))
            {
                return;
            }

            File.WriteAllText(_aliasPath,
                "# alias=music name or id\r\n" +
                "# example:\r\n" +
                "# 吉吉=ジングルベル\r\n" +
                "# ppp=411\r\n",
                Encoding.UTF8);
        }

        private void EnsureBlacklistFile()
        {
            if (File.Exists(_blacklistPath))
            {
                return;
            }

            File.WriteAllText(_blacklistPath,
                "# One music id per line. Lines starting with # are ignored.\r\n" +
                "# Example:\r\n" +
                "# 8\r\n",
                Encoding.UTF8);
        }

        private void LoadAliases()
        {
            try
            {
                var map = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(_aliasPath, Encoding.UTF8))
                {
                    var text = line.Trim();
                    if (text.Length == 0 || text.StartsWith("#") || !text.Contains("="))
                    {
                        continue;
                    }

                    var idx = text.IndexOf('=');
                    var key = Normalize(text.Substring(0, idx));
                    var value = text.Substring(idx + 1).Trim();
                    if (key.Length > 0 && value.Length > 0)
                    {
                        map[key] = value;
                    }
                }

                _aliases = map;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Failed to load aliases: " + ex.Message);
            }
        }

        private void LoadBlacklist()
        {
            try
            {
                var set = new HashSet<int>();
                foreach (var line in File.ReadAllLines(_blacklistPath, Encoding.UTF8))
                {
                    var text = line.Trim();
                    if (text.Length == 0 || text.StartsWith("#"))
                    {
                        continue;
                    }

                    var comment = text.IndexOf('#');
                    if (comment >= 0)
                    {
                        text = text.Substring(0, comment).Trim();
                    }

                    int id;
                    if (int.TryParse(text, out id) && id > 0)
                    {
                        set.Add(id);
                    }
                }

                _blacklist = set;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Failed to load blacklist: " + ex.Message);
            }
        }

        private bool IsBlacklisted(int id)
        {
            return _blacklist.Contains(id) || _blacklist.Contains(NormalizeMusicId(id));
        }

        private static bool IsAdmin(string userId, string userName)
        {
            return string.Equals(userId, "admin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(userName, "admin", StringComparison.OrdinalIgnoreCase);
        }

        private static int NormalizeMusicId(int id)
        {
            return id >= 10000 && id < 100000 ? id % 10000 : id;
        }

        private static string Normalize(string value)
        {
            if (value == null)
            {
                return "";
            }

            var sb = new StringBuilder();
            foreach (var ch in value.Trim().ToLowerInvariant())
            {
                if (!char.IsWhiteSpace(ch) && ch != '-' && ch != '_' && ch != '　')
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        private static int Distance(string a, string b)
        {
            if (a.Length == 0)
            {
                return b.Length;
            }
            if (b.Length == 0)
            {
                return a.Length;
            }

            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
            {
                prev[j] = j;
            }
            for (var i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var tmp = prev;
                prev = cur;
                cur = tmp;
            }
            return prev[b.Length];
        }
    }

    internal sealed class SongRequest
    {
        public readonly int RequestId;
        public readonly string UserId;
        public readonly string UserName;
        public readonly string Query;
        public readonly MusicInfo Music;
        public readonly DateTimeOffset CreatedAt;

        public SongRequest(int requestId, string userId, string userName, string query, MusicInfo music, DateTimeOffset createdAt)
        {
            RequestId = requestId;
            UserId = userId ?? "";
            UserName = userName ?? "";
            Query = query ?? "";
            Music = music;
            CreatedAt = createdAt;
        }
    }

    internal sealed class QueueSnapshot
    {
        public readonly List<SongRequest> Queue;
        public readonly List<SongRequest> Unresolved;
        public readonly SongRequest LastSelected;

        public QueueSnapshot(List<SongRequest> queue, List<SongRequest> unresolved, SongRequest lastSelected)
        {
            Queue = queue;
            Unresolved = unresolved;
            LastSelected = lastSelected;
        }
    }

    internal sealed class EnqueueResult
    {
        public readonly bool Success;
        public readonly string Message;
        public readonly SongRequest Request;

        private EnqueueResult(bool success, string message, SongRequest request)
        {
            Success = success;
            Message = message;
            Request = request;
        }

        public static EnqueueResult Ok(SongRequest request)
        {
            return new EnqueueResult(true, "queued", request);
        }

        public static EnqueueResult Pending(SongRequest request)
        {
            return new EnqueueResult(false, "song_unresolved", request);
        }

        public static EnqueueResult Fail(string message)
        {
            return new EnqueueResult(false, message, null);
        }
    }
}
