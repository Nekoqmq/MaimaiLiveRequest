using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MelonLoader;

namespace MaimaiLiveRequestMod
{
    internal sealed class BlivechatClient
    {
        private readonly string _url;
        private readonly string _roomCode;
        private readonly int _roomId;
        private readonly string _command;
        private readonly bool _debug;
        private readonly Action<DanmakuMessage> _onRequest;
        private Thread _thread;
        private volatile bool _stop;

        public BlivechatClient(string url, string roomCode, int roomId, string command, bool debug, Action<DanmakuMessage> onRequest)
        {
            _url = string.IsNullOrEmpty(url) ? "ws://127.0.0.1:12450/api/chat" : url;
            _roomCode = roomCode == null ? "" : roomCode.Trim();
            _roomId = roomId;
            _command = string.IsNullOrWhiteSpace(command) ? "\u70b9\u6b4c" : command.Trim();
            _debug = debug;
            _onRequest = onRequest;
        }

        public void Start()
        {
            _stop = false;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "MaimaiLiveRequestBlivechat"
            };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
        }

        private void Run()
        {
            while (!_stop)
            {
                try
                {
                    ConnectOnce();
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("blivechat disconnected: " + Describe(ex));
                }

                if (!_stop)
                {
                    Thread.Sleep(5000);
                }
            }
        }

        private void ConnectOnce()
        {
            var url = BuildUrl();
            using (var ws = new ClientWebSocket())
            {
                ws.ConnectAsync(new Uri(url), CancellationToken.None).GetAwaiter().GetResult();
                MelonLogger.Msg("blivechat connected: " + url);

                var roomKey = !string.IsNullOrEmpty(_roomCode)
                    ? "{\"type\":2,\"value\":" + Json(_roomCode) + "}"
                    : (_roomId > 0 ? "{\"type\":1,\"value\":" + _roomId + "}" : "");
                if (string.IsNullOrEmpty(roomKey))
                {
                    MelonLogger.Warning("BlivechatRoomCode and BlivechatRoomId are empty. /api/chat may close as room=None.");
                }
                else
                {
                    SendText(ws, "{\"cmd\":1,\"data\":{\"roomKey\":" + roomKey + ",\"config\":{\"autoTranslate\":false}}}");
                }

                var buffer = new byte[1024 * 64];
                while (!_stop && ws.State == WebSocketState.Open)
                {
                    using (var ms = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).GetAwaiter().GetResult();
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                MelonLogger.Warning("blivechat closed: " + ws.CloseStatus + " " + ws.CloseStatusDescription);
                                return;
                            }

                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            try
                            {
                                HandleMessage(Encoding.UTF8.GetString(ms.ToArray()), ws);
                            }
                            catch (Exception ex)
                            {
                                if (_debug)
                                {
                                    MelonLogger.Warning("blivechat parse error: " + Describe(ex));
                                }
                            }
                        }
                    }
                }
            }
        }

        private string BuildUrl()
        {
            return _url;
        }

        private void HandleMessage(string json, ClientWebSocket ws)
        {
            var cmd = JsonNumber(json, "cmd");
            if (cmd == "0")
            {
                SendText(ws, "{\"cmd\":0}");
                return;
            }

            if (cmd == "2")
            {
                var text = DataArrayString(json, 4);
                var uid = DataArrayString(json, 16);
                var name = DataArrayString(json, 2);
                if (_debug)
                {
                    MelonLogger.Msg("blivechat text: " + name + " -> " + text);
                }
                SubmitRequest(text, uid, name);
                return;
            }

            ParseMessage(json);
        }

        private void ParseMessage(string json)
        {
            var text = FirstJsonString(json, "msg", "message", "text", "content", "comment");
            if (string.IsNullOrEmpty(text) && JsonString(json, "cmd").StartsWith("DANMU_MSG"))
            {
                text = DanmakuText(json);
            }

            var uid = FirstJsonScalar(json, "uid", "user_id", "userId");
            var name = FirstJsonString(json, "uname", "user_name", "nickname", "name", "user");
            SubmitRequest(text, uid, name);
        }

        private void SubmitRequest(string text, string uid, string name)
        {
            text = (text ?? "").Trim();
            if (text.Length < _command.Length || !text.StartsWith(_command, StringComparison.Ordinal))
            {
                if (_debug && text.Length > 0)
                {
                    MelonLogger.Msg("blivechat ignored by command: expected " + _command + ", got " + text);
                }
                return;
            }

            var query = text.Length == _command.Length ? "" : text.Substring(_command.Length).Trim();
            if (query.Length == 0)
            {
                return;
            }

            if (string.IsNullOrEmpty(uid))
            {
                uid = string.IsNullOrEmpty(name) ? "blivechat" : name;
            }
            if (string.IsNullOrEmpty(name))
            {
                name = uid;
            }

            if (_debug)
            {
                MelonLogger.Msg("blivechat request: " + name + " -> " + query);
            }
            _onRequest(new DanmakuMessage(uid, name, query));
        }

        private static void SendText(ClientWebSocket ws, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
        }

        private static string FirstJsonString(string json, params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = JsonString(json, key);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            return "";
        }

        private static string FirstJsonScalar(string json, params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = JsonString(json, key);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }

                value = JsonNumber(json, key);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            return "";
        }

        private static string JsonString(string json, string key)
        {
            var match = Regex.Match(json ?? "", "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"", RegexOptions.CultureInvariant);
            return match.Success ? UnescapeJson(match.Groups["v"].Value) : "";
        }

        private static string JsonNumber(string json, string key)
        {
            var match = Regex.Match(json ?? "", "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>\\d+)", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["v"].Value : "";
        }

        private static string DataArrayString(string json, int index)
        {
            var key = "\"data\"";
            var keyIndex = (json ?? "").IndexOf(key, StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                return "";
            }

            var arrayStart = json.IndexOf('[', keyIndex + key.Length);
            if (arrayStart < 0)
            {
                return "";
            }

            var itemStart = arrayStart + 1;
            var depth = 0;
            var inString = false;
            var escaped = false;
            var current = 0;
            for (var i = itemStart; i < json.Length; i++)
            {
                var ch = json[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                }
                else if (ch == '[' || ch == '{')
                {
                    depth++;
                }
                else if (ch == ']' && depth == 0)
                {
                    return current == index ? JsonTokenToString(json.Substring(itemStart, i - itemStart)) : "";
                }
                else if (ch == ']' || ch == '}')
                {
                    depth--;
                }
                else if (ch == ',' && depth == 0)
                {
                    if (current == index)
                    {
                        return JsonTokenToString(json.Substring(itemStart, i - itemStart));
                    }
                    current++;
                    itemStart = i + 1;
                }
            }

            return "";
        }

        private static string JsonTokenToString(string token)
        {
            token = (token ?? "").Trim();
            if (token.Length >= 2 && token[0] == '"' && token[token.Length - 1] == '"')
            {
                return UnescapeJson(token.Substring(1, token.Length - 2));
            }
            return token == "null" ? "" : token;
        }

        private static string DanmakuText(string json)
        {
            var match = Regex.Match(json ?? "", "\"info\"\\s*:\\s*\\[.*?,\\s*\"(?<v>(?:\\\\.|[^\"])*)\"", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            return match.Success ? UnescapeJson(match.Groups["v"].Value) : "";
        }

        private static string UnescapeJson(string text)
        {
            var value = Regex.Replace(text ?? "", "\\\\u(?<h>[0-9a-fA-F]{4})", m => ((char)Convert.ToInt32(m.Groups["h"].Value, 16)).ToString(), RegexOptions.CultureInvariant);
            return value
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\/", "/")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }

        private static string Json(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string Describe(Exception ex)
        {
            var aggregate = ex as AggregateException;
            if (aggregate != null && aggregate.InnerException != null)
            {
                return Describe(aggregate.InnerException);
            }

            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}
