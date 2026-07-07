using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MelonLoader;

namespace MaimaiLiveRequestMod
{
    internal sealed class BiliDanmakuClient
    {
        private readonly int _roomId;
        private readonly string _command;
        private readonly bool _debug;
        private readonly Action<DanmakuMessage> _onRequest;
        private Thread _thread;
        private volatile bool _stop;

        public BiliDanmakuClient(int roomId, string command, bool debug, Action<DanmakuMessage> onRequest)
        {
            _roomId = roomId;
            _command = command;
            _debug = debug;
            _onRequest = onRequest;
        }

        public void Start()
        {
            _stop = false;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "MaimaiLiveRequestBili"
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
                    MelonLogger.Warning("Bili danmaku disconnected: " + Describe(ex));
                }

                if (!_stop)
                {
                    Thread.Sleep(5000);
                }
            }
        }

        private void ConnectOnce()
        {
            var info = GetDanmuInfo();
            using (var ws = new ClientWebSocket())
            {
                ws.Options.SetRequestHeader("Origin", "https://live.bilibili.com");
                ws.ConnectAsync(new Uri(info.WebSocketUrl), CancellationToken.None).GetAwaiter().GetResult();
                SendPacket(ws, 7, "{\"uid\":0,\"roomid\":" + info.RoomId + ",\"protover\":2,\"platform\":\"web\",\"type\":2,\"key\":\"" + JsonEscape(info.Token) + "\"}");
                MelonLogger.Msg("Bili danmaku connected: room " + info.RoomId);

                var heartbeat = new Thread(() =>
                {
                    while (!_stop && ws.State == WebSocketState.Open)
                    {
                        try
                        {
                            SendPacket(ws, 2, "");
                        }
                        catch
                        {
                            break;
                        }
                        Thread.Sleep(30000);
                    }
                })
                {
                    IsBackground = true
                };
                heartbeat.Start();

                var buffer = new byte[1024 * 256];
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
                                MelonLogger.Warning("Bili danmaku closed: " + ws.CloseStatus + " " + ws.CloseStatusDescription);
                                return;
                            }
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        ParsePackets(ms.ToArray());
                    }
                }
            }
        }

        private DanmuInfo GetDanmuInfo()
        {
            var roomId = ResolveRoomId(_roomId);
            var json = HttpGet("https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?id=" + roomId + "&type=0");
            var token = JsonString(json, "token");
            var actualRoomId = JsonInt(json, "room_id");
            var host = "broadcastlv.chat.bilibili.com";
            var port = 443;

            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException("Bili getDanmuInfo returned empty token: code=" + JsonInt(json, "code") + ", message=" + JsonString(json, "message") + ", body=" + Short(json));
            }

            var hostListIndex = json.IndexOf("\"host_list\"", StringComparison.Ordinal);
            if (hostListIndex >= 0)
            {
                var tail = json.Substring(hostListIndex);
                var parsedHost = JsonString(tail, "host");
                var parsedPort = JsonInt(tail, "wss_port");
                if (!string.IsNullOrEmpty(parsedHost))
                {
                    host = parsedHost;
                }
                if (parsedPort > 0)
                {
                    port = parsedPort;
                }
            }

            return new DanmuInfo(actualRoomId > 0 ? actualRoomId : roomId, token, "wss://" + host + ":" + port + "/sub");
        }

        private static int ResolveRoomId(int roomId)
        {
            var json = HttpGet("https://api.live.bilibili.com/room/v1/Room/room_init?id=" + roomId);
            var actualRoomId = JsonInt(json, "room_id");
            return actualRoomId > 0 ? actualRoomId : roomId;
        }

        private void ParsePackets(byte[] data)
        {
            var offset = 0;
            while (offset + 16 <= data.Length)
            {
                var packetLen = ReadInt(data, offset);
                var headerLen = ReadShort(data, offset + 4);
                var proto = ReadShort(data, offset + 6);
                var op = ReadInt(data, offset + 8);
                if (packetLen <= 0 || offset + packetLen > data.Length)
                {
                    return;
                }

                var bodyLen = packetLen - headerLen;
                var body = new byte[bodyLen];
                Buffer.BlockCopy(data, offset + headerLen, body, 0, bodyLen);

                if (op == 8)
                {
                    var auth = Encoding.UTF8.GetString(body);
                    if (JsonInt(auth, "code") != 0)
                    {
                        throw new InvalidOperationException("Bili auth failed: " + auth);
                    }
                }
                else if (op == 5)
                {
                    if (proto == 2)
                    {
                        var inflated = InflateZlib(body);
                        if (inflated != null)
                        {
                            ParsePackets(inflated);
                        }
                    }
                    else
                    {
                        ParseCommand(Encoding.UTF8.GetString(body));
                    }
                }

                offset += packetLen;
            }
        }

        private void ParseCommand(string json)
        {
            try
            {
                if (!JsonString(json, "cmd").StartsWith("DANMU_MSG"))
                {
                    return;
                }

                var text = DanmakuText(json).Trim();
                if (!text.StartsWith(_command))
                {
                    return;
                }

                var query = text.Substring(_command.Length).Trim();
                if (query.Length == 0)
                {
                    return;
                }

                var user = DanmakuUser(json);
                var uid = user.Item1;
                var name = string.IsNullOrEmpty(user.Item2) ? uid : user.Item2;
                _onRequest(new DanmakuMessage(uid, name, query));
            }
            catch (Exception ex)
            {
                if (_debug)
                {
                    MelonLogger.Warning("Bili parse error: " + ex.Message);
                }
            }
        }

        private static void SendPacket(ClientWebSocket ws, int op, string bodyText)
        {
            var body = Encoding.UTF8.GetBytes(bodyText ?? "");
            var packet = new byte[16 + body.Length];
            WriteInt(packet, 0, packet.Length);
            WriteShort(packet, 4, 16);
            WriteShort(packet, 6, 1);
            WriteInt(packet, 8, op);
            WriteInt(packet, 12, 1);
            Buffer.BlockCopy(body, 0, packet, 16, body.Length);
            ws.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None).GetAwaiter().GetResult();
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

        private static byte[] InflateZlib(byte[] data)
        {
            try
            {
                using (var input = new MemoryStream(data, 2, data.Length - 6))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    deflate.CopyTo(output);
                    return output.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string HttpGet(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0";
            req.Accept = "application/json, text/plain, */*";
            req.Referer = "https://live.bilibili.com/";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static string Short(string text)
        {
            text = (text ?? "").Replace("\r", "").Replace("\n", "");
            return text.Length <= 240 ? text : text.Substring(0, 240);
        }

        private static int ReadInt(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }

        private static short ReadShort(byte[] data, int offset)
        {
            return (short)((data[offset] << 8) | data[offset + 1]);
        }

        private static void WriteInt(byte[] data, int offset, int value)
        {
            data[offset] = (byte)((value >> 24) & 0xff);
            data[offset + 1] = (byte)((value >> 16) & 0xff);
            data[offset + 2] = (byte)((value >> 8) & 0xff);
            data[offset + 3] = (byte)(value & 0xff);
        }

        private static void WriteShort(byte[] data, int offset, short value)
        {
            data[offset] = (byte)((value >> 8) & 0xff);
            data[offset + 1] = (byte)(value & 0xff);
        }

        private static string JsonEscape(string text)
        {
            return (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string JsonString(string json, string key)
        {
            var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"", RegexOptions.CultureInvariant);
            return match.Success ? UnescapeJson(match.Groups["v"].Value) : "";
        }

        private static int JsonInt(string json, string key)
        {
            var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>\\d+)", RegexOptions.CultureInvariant);
            int value;
            return match.Success && int.TryParse(match.Groups["v"].Value, out value) ? value : 0;
        }

        private static string DanmakuText(string json)
        {
            var match = Regex.Match(json, "\"info\"\\s*:\\s*\\[.*?,\\s*\"(?<v>(?:\\\\.|[^\"])*)\"", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            return match.Success ? UnescapeJson(match.Groups["v"].Value) : "";
        }

        private static Tuple<string, string> DanmakuUser(string json)
        {
            var match = Regex.Match(json, "\"info\"\\s*:\\s*\\[.*?,\\s*\"(?:\\\\.|[^\"]*)\"\\s*,\\s*\\[\\s*(?<uid>\\d+)\\s*,\\s*\"(?<name>(?:\\\\.|[^\"])*)\"", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            return match.Success
                ? Tuple.Create(match.Groups["uid"].Value, UnescapeJson(match.Groups["name"].Value))
                : Tuple.Create("", "");
        }

        private static string UnescapeJson(string text)
        {
            return (text ?? "")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\/", "/")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }
    }

    internal sealed class DanmakuMessage
    {
        public readonly string UserId;
        public readonly string UserName;
        public readonly string Text;

        public DanmakuMessage(string userId, string userName, string text)
        {
            UserId = userId ?? "";
            UserName = userName ?? "";
            Text = text ?? "";
        }
    }

    internal sealed class DanmuInfo
    {
        public readonly int RoomId;
        public readonly string Token;
        public readonly string WebSocketUrl;

        public DanmuInfo(int roomId, string token, string webSocketUrl)
        {
            RoomId = roomId;
            Token = token;
            WebSocketUrl = webSocketUrl;
        }
    }
}
