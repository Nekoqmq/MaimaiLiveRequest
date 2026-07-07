using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using MelonLoader;

namespace MaimaiLiveRequestMod
{
    internal sealed class OverlayServer
    {
        private readonly string _prefix;
        private readonly RequestQueue _queue;
        private readonly string _htmlPath;
        private HttpListener _listener;
        private Thread _thread;

        public OverlayServer(string prefix, RequestQueue queue, string htmlPath)
        {
            _prefix = prefix.EndsWith("/") ? prefix : prefix + "/";
            _queue = queue;
            _htmlPath = htmlPath;
        }

        public void Start()
        {
            EnsureHtml();
            _listener = new HttpListener();
            _listener.Prefixes.Add(_prefix);
            _listener.Start();
            _thread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "MaimaiLiveRequestBoard"
            };
            _thread.Start();
        }

        public void Stop()
        {
            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                    _listener.Close();
                }
            }
            catch
            {
                // Ignored during shutdown.
            }
            _listener = null;
            _thread = null;
        }

        private void ListenLoop()
        {
            while (_listener != null && _listener.IsListening)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => Handle(context));
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("Board server error: " + ex.Message);
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            try
            {
                AddCors(context.Response);
                var path = context.Request.Url == null ? "/" : context.Request.Url.AbsolutePath.TrimEnd('/');
                if (path.Length == 0)
                {
                    path = "/";
                }

                if (context.Request.HttpMethod == "GET" && (path == "/" || path == "/board"))
                {
                    Write(context.Response, "text/html; charset=utf-8", File.ReadAllText(_htmlPath, Encoding.UTF8));
                    return;
                }

                if (context.Request.HttpMethod == "GET" && path == "/admin")
                {
                    Write(context.Response, "text/html; charset=utf-8", AdminHtml());
                    return;
                }

                if (context.Request.HttpMethod == "GET" && path == "/api/queue")
                {
                    Write(context.Response, "application/json; charset=utf-8", QueueJson());
                    return;
                }

                if (context.Request.HttpMethod == "GET" && path == "/api/notice")
                {
                    Write(context.Response, "application/json; charset=utf-8", NoticeJson());
                    return;
                }

                if (context.Request.HttpMethod == "GET" && path == "/api/notice-logo")
                {
                    WriteNoticeLogo(context.Response);
                    return;
                }

                if (context.Request.HttpMethod == "GET" && path == "/api/health")
                {
                    Write(context.Response, "application/json; charset=utf-8", LiveRequestService.HealthJson());
                    return;
                }

                if ((context.Request.HttpMethod == "GET" || context.Request.HttpMethod == "POST") && path == "/api/refresh")
                {
                    Write(context.Response, "application/json; charset=utf-8", LiveRequestService.RefreshCatalogJson());
                    return;
                }

                if (context.Request.HttpMethod == "GET" && path == "/api/admin/settings")
                {
                    Write(context.Response, "application/json; charset=utf-8", SettingsJson());
                    return;
                }

                if (context.Request.HttpMethod == "POST" && path == "/api/admin/settings")
                {
                    Write(context.Response, "application/json; charset=utf-8", SaveSettingsJson(context.Request));
                    return;
                }

                if ((context.Request.HttpMethod == "GET" || context.Request.HttpMethod == "POST") && path == "/api/admin/reload-config")
                {
                    _queue.ReloadFiles();
                    EnsureNotice();
                    Write(context.Response, "application/json; charset=utf-8", "{\"success\":true,\"message\":\"reloaded\"}");
                    return;
                }

                if ((context.Request.HttpMethod == "GET" || context.Request.HttpMethod == "POST") && path == "/api/admin/remove")
                {
                    int id;
                    if (!int.TryParse(context.Request.QueryString["id"], out id))
                    {
                        Write(context.Response, "application/json; charset=utf-8", "{\"success\":false,\"message\":\"bad_request_id\"}", 400);
                        return;
                    }

                    Write(context.Response, "application/json; charset=utf-8", ResultJson(_queue.Remove(id)));
                    return;
                }

                if ((context.Request.HttpMethod == "GET" || context.Request.HttpMethod == "POST") && path == "/api/admin/select-next")
                {
                    Write(context.Response, "application/json; charset=utf-8", ResultJson(LiveRequestService.SelectNextNow()));
                    return;
                }

                if ((context.Request.HttpMethod == "GET" || context.Request.HttpMethod == "POST") && path == "/api/admin/resolve")
                {
                    int id;
                    if (!int.TryParse(context.Request.QueryString["id"], out id))
                    {
                        Write(context.Response, "application/json; charset=utf-8", "{\"success\":false,\"message\":\"bad_request_id\"}", 400);
                        return;
                    }

                    var q = context.Request.QueryString["q"];
                    if (string.IsNullOrEmpty(q) && context.Request.HttpMethod == "POST")
                    {
                        using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                        {
                            q = reader.ReadToEnd();
                        }
                    }

                    Write(context.Response, "application/json; charset=utf-8", ResultJson(LiveRequestService.ResolveRequest(id, q)));
                    return;
                }

                if ((context.Request.HttpMethod == "GET" || context.Request.HttpMethod == "POST") && path == "/api/admin/move")
                {
                    int id;
                    if (!int.TryParse(context.Request.QueryString["id"], out id))
                    {
                        Write(context.Response, "application/json; charset=utf-8", "{\"success\":false,\"message\":\"bad_request_id\"}", 400);
                        return;
                    }

                    int to;
                    var toText = context.Request.QueryString["to"];
                    if (int.TryParse(toText, out to))
                    {
                        Write(context.Response, "application/json; charset=utf-8", ResultJson(_queue.Move(id, to)));
                        return;
                    }

                    var dir = (context.Request.QueryString["dir"] ?? "").ToLowerInvariant();
                    var delta = dir == "up" ? -1 : dir == "down" ? 1 : 0;
                    if (delta == 0)
                    {
                        Write(context.Response, "application/json; charset=utf-8", "{\"success\":false,\"message\":\"bad_direction\"}", 400);
                        return;
                    }

                    Write(context.Response, "application/json; charset=utf-8", ResultJson(_queue.MoveByDelta(id, delta)));
                    return;
                }

                if ((context.Request.HttpMethod == "GET" || context.Request.HttpMethod == "POST") && path == "/api/request")
                {
                    var user = context.Request.QueryString["user"];
                    var uid = context.Request.QueryString["uid"];
                    var q = context.Request.QueryString["q"];
                    if (string.IsNullOrEmpty(q) && context.Request.HttpMethod == "POST")
                    {
                        using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                        {
                            q = reader.ReadToEnd();
                        }
                    }

                    var result = LiveRequestService.Enqueue(string.IsNullOrEmpty(uid) ? user : uid, user, q);
                    Write(context.Response, "application/json; charset=utf-8", ResultJson(result));
                    return;
                }

                Write(context.Response, "text/plain; charset=utf-8", "not_found", 404);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Board request error: " + ex.Message);
                try
                {
                    context.Response.Close();
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
        }

        private string QueueJson()
        {
            var snapshot = _queue.GetSnapshot();
            var sb = new StringBuilder();
            sb.Append("{\"lastSelected\":");
            AppendRequest(sb, snapshot.LastSelected);
            sb.Append(",\"queue\":[");
            for (var i = 0; i < snapshot.Queue.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(",");
                }
                AppendRequest(sb, snapshot.Queue[i]);
            }
            sb.Append("],\"unresolved\":[");
            for (var i = 0; i < snapshot.Unresolved.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(",");
                }
                AppendRequest(sb, snapshot.Unresolved[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string NoticeJson()
        {
            EnsureNotice();

            var flashSeconds = 5;
            var logoWidth = 172;
            var logoHeight = 156;
            var logoX = 0;
            var logoY = 0;
            var logoScale = 1.0;
            var lines = new List<string>();
            foreach (var raw in File.ReadAllLines(NoticePath(), Encoding.UTF8))
            {
                var line = (raw ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }

                if (line.StartsWith("flashSeconds=", StringComparison.OrdinalIgnoreCase))
                {
                    int seconds;
                    if (int.TryParse(line.Substring("flashSeconds=".Length).Trim(), out seconds) && seconds >= 1)
                    {
                        flashSeconds = seconds;
                    }
                    continue;
                }

                int intValue;
                if (TryIntSetting(line, "logoWidth", out intValue))
                {
                    logoWidth = Math.Max(1, intValue);
                    continue;
                }
                if (TryIntSetting(line, "logoHeight", out intValue))
                {
                    logoHeight = Math.Max(1, intValue);
                    continue;
                }
                if (TryIntSetting(line, "logoX", out intValue))
                {
                    logoX = intValue;
                    continue;
                }
                if (TryIntSetting(line, "logoY", out intValue))
                {
                    logoY = intValue;
                    continue;
                }

                double doubleValue;
                if (TryDoubleSetting(line, "logoScale", out doubleValue))
                {
                    logoScale = Math.Max(0.05, Math.Min(5.0, doubleValue));
                    continue;
                }

                if (line.StartsWith("line=", StringComparison.OrdinalIgnoreCase))
                {
                    line = line.Substring("line=".Length).Trim();
                }
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }

            var sb = new StringBuilder();
            sb.Append("{\"flashSeconds\":").Append(flashSeconds).Append(",\"logoUrl\":\"/api/notice-logo\",\"lines\":[");
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(",");
                }
                sb.Append(Json(lines[i]));
            }
            sb.Append("],\"logo\":{");
            sb.Append("\"width\":").Append(logoWidth).Append(",");
            sb.Append("\"height\":").Append(logoHeight).Append(",");
            sb.Append("\"x\":").Append(logoX).Append(",");
            sb.Append("\"y\":").Append(logoY).Append(",");
            sb.Append("\"scale\":").Append(logoScale.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append("}}");
            return sb.ToString();
        }

        private string SettingsJson()
        {
            EnsureNotice();
            return "{"
                + "\"aliases\":" + Json(ReadText(AliasPath())) + ","
                + "\"blacklist\":" + Json(ReadText(BlacklistPath())) + ","
                + "\"notice\":" + Json(ReadText(NoticePath())) + ","
                + "\"autoSelect\":" + (LiveRequestService.AutoSelectEnabled ? "true" : "false")
                + "}";
        }

        private string SaveSettingsJson(HttpListenerRequest request)
        {
            var form = ReadForm(request);
            WriteText(AliasPath(), GetForm(form, "aliases"));
            WriteText(BlacklistPath(), GetForm(form, "blacklist"));
            WriteText(NoticePath(), GetForm(form, "notice"));
            if (form.ContainsKey("autoSelect"))
            {
                LiveRequestService.SetAutoSelect(IsTrue(GetForm(form, "autoSelect")));
            }
            _queue.ReloadFiles();
            return "{\"success\":true,\"message\":\"saved\",\"autoSelect\":" + (LiveRequestService.AutoSelectEnabled ? "true" : "false") + "}";
        }

        private static void AppendRequest(StringBuilder sb, SongRequest request)
        {
            if (request == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append("{");
            sb.Append("\"requestId\":").Append(request.RequestId).Append(",");
            sb.Append("\"userName\":").Append(Json(request.UserName)).Append(",");
            sb.Append("\"query\":").Append(Json(request.Query)).Append(",");
            if (request.Music == null)
            {
                sb.Append("\"music\":null}");
                return;
            }

            sb.Append("\"music\":{");
            sb.Append("\"id\":").Append(request.Music.Id).Append(",");
            sb.Append("\"name\":").Append(Json(request.Music.Name)).Append(",");
            sb.Append("\"artist\":").Append(Json(request.Music.Artist));
            sb.Append("}}");
        }

        private static string ResultJson(EnqueueResult result)
        {
            var sb = new StringBuilder();
            sb.Append("{\"success\":").Append(result.Success ? "true" : "false");
            sb.Append(",\"message\":").Append(Json(result.Message));
            if (result.Request != null)
            {
                sb.Append(",\"request\":");
                AppendRequest(sb, result.Request);
            }
            sb.Append("}");
            return sb.ToString();
        }

        private static void Write(HttpListenerResponse response, string contentType, string body, int statusCode = 200)
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? "");
            WriteBytes(response, contentType, bytes, statusCode);
        }

        private static void WriteBytes(HttpListenerResponse response, string contentType, byte[] bytes, int statusCode = 200)
        {
            bytes = bytes ?? new byte[0];
            response.Headers["Cache-Control"] = "no-store";
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            if (bytes.Length > 0)
            {
                response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            response.OutputStream.Close();
        }

        private void WriteNoticeLogo(HttpListenerResponse response)
        {
            var path = FindNoticeLogo();
            if (!string.IsNullOrEmpty(path))
            {
                WriteBytes(response, ContentType(path), File.ReadAllBytes(path));
                return;
            }

            var svg = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 256 256""><defs><linearGradient id=""g"" x1=""0"" y1=""0"" x2=""1"" y2=""1""><stop stop-color=""#ff6b35""/><stop offset=""1"" stop-color=""#7c3aed""/></linearGradient></defs><rect width=""256"" height=""256"" rx=""54"" fill=""#181024""/><circle cx=""128"" cy=""100"" r=""46"" fill=""url(#g)""/><path d=""M54 218c12-48 50-75 74-75s62 27 74 75"" fill=""none"" stroke=""url(#g)"" stroke-width=""22"" stroke-linecap=""round""/><text x=""128"" y=""238"" text-anchor=""middle"" font-family=""Arial,sans-serif"" font-size=""28"" font-weight=""700"" fill=""#f7f4ea"">LIVE</text></svg>";
            WriteBytes(response, "image/svg+xml", Encoding.UTF8.GetBytes(svg));
        }

        private string FindNoticeLogo()
        {
            var dir = ModDirectory();
            foreach (var name in new[] { "MaimaiLiveRequestLogo.png", "MaimaiLiveRequestLogo.jpg", "MaimaiLiveRequestLogo.jpeg", "MaimaiLiveRequestLogo.webp", "MaimaiLiveRequestLogo.gif", "MaimaiLiveRequestLogo.svg" })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            return "";
        }

        private static string ContentType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".png") return "image/png";
            if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
            if (ext == ".webp") return "image/webp";
            if (ext == ".gif") return "image/gif";
            if (ext == ".svg") return "image/svg+xml";
            return "application/octet-stream";
        }

        private static void AddCors(HttpListenerResponse response)
        {
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        }

        private static string Json(string value)
        {
            var text = value ?? "";
            var sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            foreach (var ch in text)
            {
                switch (ch)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static bool TryIntSetting(string line, string key, out int value)
        {
            value = 0;
            var prefix = key + "=";
            return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.Substring(prefix.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryDoubleSetting(string line, string key, out double value)
        {
            value = 0;
            var prefix = key + "=";
            return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && double.TryParse(line.Substring(prefix.Length).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static Dictionary<string, string> ReadForm(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                var body = reader.ReadToEnd();
                var map = new Dictionary<string, string>();
                foreach (var part in body.Split('&'))
                {
                    if (part.Length == 0)
                    {
                        continue;
                    }

                    var index = part.IndexOf('=');
                    var key = index < 0 ? part : part.Substring(0, index);
                    var value = index < 0 ? "" : part.Substring(index + 1);
                    map[UrlDecode(key)] = UrlDecode(value);
                }
                return map;
            }
        }

        private static string UrlDecode(string value)
        {
            return WebUtility.UrlDecode((value ?? "").Replace("+", " ")) ?? "";
        }

        private static string GetForm(Dictionary<string, string> form, string key)
        {
            string value;
            return form.TryGetValue(key, out value) ? value : "";
        }

        private static bool IsTrue(string value)
        {
            value = (value ?? "").Trim();
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || value == "1"
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadText(string path)
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
        }

        private static void WriteText(string path, string value)
        {
            File.WriteAllText(path, value ?? "", Encoding.UTF8);
        }

        private static string AdminHtmlV2()
        {
            return @"<!doctype html>
<html lang=""zh-CN"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width,initial-scale=1"">
  <title>maimai 点歌管理</title>
  <style>
    body { margin: 0; background: #101719; color: #f3f6ef; font-family: ""Microsoft YaHei UI"", sans-serif; }
    main { max-width: 1120px; margin: 0 auto; padding: 24px; }
    h1 { margin: 0 0 16px; font-size: 28px; }
    h2 { margin: 24px 0 12px; }
    .tabs, .bar { display: flex; gap: 8px; margin-bottom: 16px; flex-wrap: wrap; }
    .tab { background: #263438; color: #fff; }
    .tab.active { background: #6ee7f2; color: #071013; }
    input, textarea { flex: 1; padding: 10px 12px; border: 1px solid #405057; border-radius: 8px; background: #192326; color: #fff; font: inherit; }
    input[type=checkbox] { flex: 0 0 auto; min-width: 0; width: 20px; height: 20px; }
    textarea { width: 100%; min-height: 150px; resize: vertical; box-sizing: border-box; font-family: Consolas, ""Microsoft YaHei UI"", monospace; }
    button { padding: 8px 12px; border: 0; border-radius: 8px; background: #6ee7f2; color: #071013; cursor: pointer; font-weight: 700; }
    button.warn { background: #ff7f6e; }
    button.fix { background: #ffb15c; }
    button.gray { background: #46555b; color: #fff; }
    .box { padding: 14px; margin-top: 16px; border: 1px solid rgba(255,255,255,.12); border-radius: 14px; background: #141f22; }
    .unresolvedBox { border-color: rgba(255,107,53,.78); background: linear-gradient(145deg, rgba(72,31,17,.92), rgba(32,20,47,.88)); }
    .now, .row { display: grid; grid-template-columns: 1fr auto; gap: 12px; align-items: center; padding: 12px; margin-bottom: 10px; border: 1px solid rgba(255,255,255,.12); border-radius: 10px; background: #182225; }
    .unresolvedBox .row { border-color: rgba(255,177,92,.45); background: rgba(255,177,92,.08); }
    .now { border-color: #ffdc5e; background: #272414; }
    .name { font-size: 18px; font-weight: 700; }
    .meta, .hint, #settingsStatus { color: #aeb8b4; margin-top: 4px; }
    .actions { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
    .edit { width: 220px; flex: 0 0 220px; }
    .empty { color: #aeb8b4; padding: 24px; border: 1px dashed rgba(255,255,255,.18); border-radius: 10px; }
    .hidden { display: none; }
    .logoGrid { display: grid; grid-template-columns: minmax(320px, 1fr) 360px; gap: 16px; align-items: start; }
    .logoControl { display: grid; grid-template-columns: 120px 1fr 70px; gap: 10px; align-items: center; margin: 10px 0; }
    .logoControl input[type=range] { padding: 0; }
    .logoPreview { position: relative; height: 220px; overflow: visible; border: 1px dashed rgba(255,255,255,.25); border-radius: 14px; background: linear-gradient(135deg, rgba(255,255,255,.06), rgba(255,255,255,.015)); }
    .logoPreview::after { content: 'right bottom anchor'; position: absolute; right: 10px; bottom: 8px; color: #758387; font-size: 12px; }
    .logoPreviewImg { position: absolute; right: 0; bottom: 0; width: var(--lw, 172px); height: var(--lh, 156px); object-fit: contain; transform: translate(var(--lx, 0px), var(--ly, 0px)) scale(var(--ls, 1)); transform-origin: right bottom; filter: drop-shadow(0 16px 26px rgba(0,0,0,.45)); }
    .credit { margin: 28px 0 4px; text-align: center; color: #7f8f93; font-size: 13px; letter-spacing: .08em; }
    .check { display: flex; gap: 10px; align-items: center; font-size: 16px; }
  </style>
</head>
<body>
<main>
  <h1>maimai 点歌管理</h1>
  <nav class=""tabs"">
    <button class=""tab active"" id=""tabQueueBtn"" onclick=""showTab('queue')"">队列</button>
    <button class=""tab"" id=""tabSettingsBtn"" onclick=""showTab('settings')"">设置</button>
    <button class=""tab"" id=""tabLogoBtn"" onclick=""showTab('logo')"">Logo</button>
  </nav>
  <section id=""queueTab"">
    <div class=""bar"">
      <input id=""q"" placeholder=""手动点歌：曲名、别名或 ID"">
      <button onclick=""add()"">加入队列</button>
      <button onclick=""selectNext()"">切到下一首</button>
      <button class=""gray"" onclick=""tick()"">刷新队列</button>
    </div>
    <section id=""now""></section>
    <section class=""box"" id=""list""></section>
    <section class=""box unresolvedBox"" id=""unresolved""></section>
  </section>
  <section id=""settingsTab"" class=""hidden"">
    <div class=""bar"">
      <button onclick=""loadSettings()"">重新加载配置</button>
      <button class=""fix"" onclick=""saveSettings()"">保存配置</button>
      <button class=""gray"" onclick=""reloadConfig()"">应用到点歌</button>
      <button class=""gray"" onclick=""refreshCatalog()"">刷新曲库</button>
    </div>
    <div id=""settingsStatus"">未加载</div>
    <section class=""box"">
      <h2>自动切歌</h2>
      <label class=""check""><input id=""autoSelectCheck"" type=""checkbox"">启用自动切到队列下一首</label>
      <div class=""hint"">关闭后不会自动切歌，仍可在队列页点“切到下一首”。保存后立刻生效。</div>
    </section>
    <section class=""box"">
      <h2>别名表 MaimaiSongAliases.txt</h2>
      <div class=""hint"">格式：别名=正式曲名或歌曲ID</div>
      <textarea id=""aliasesText""></textarea>
    </section>
    <section class=""box"">
      <h2>黑名单 MaimaiSongBlacklist.txt</h2>
      <div class=""hint"">一行一个歌曲 ID，# 开头为注释</div>
      <textarea id=""blacklistText""></textarea>
    </section>
    <section class=""box"">
      <h2>看板提示与 Logo MaimaiLiveRequestBoardNotice.txt</h2>
      <div class=""hint"">logoWidth/logoHeight 是展示分辨率；logoX/logoY 从右下角偏移；logoScale 是缩放倍率。</div>
      <textarea id=""noticeTextArea""></textarea>
    </section>
  </section>
  <section id=""logoTab"" class=""hidden"">
    <div class=""bar"">
      <button onclick=""loadSettings().then(syncLogoControls)"">重新加载</button>
      <button class=""fix"" onclick=""saveSettings()"">保存 Logo 设置</button>
      <button class=""gray"" onclick=""resetLogoControls()"">恢复默认值</button>
    </div>
    <section class=""box logoGrid"">
      <div>
        <h2>Logo 位置与大小</h2>
        <div class=""hint"">滑条会实时预览，并同步写入右侧设置文本；保存后 OBS 看板刷新即可生效。</div>
        <div class=""logoControl""><label>展示宽度</label><input id=""logoWidthRange"" type=""range"" min=""40"" max=""800"" step=""1"" oninput=""logoSliderChanged()""><input id=""logoWidthValue"" type=""number"" min=""1"" oninput=""logoNumberChanged()""></div>
        <div class=""logoControl""><label>展示高度</label><input id=""logoHeightRange"" type=""range"" min=""40"" max=""800"" step=""1"" oninput=""logoSliderChanged()""><input id=""logoHeightValue"" type=""number"" min=""1"" oninput=""logoNumberChanged()""></div>
        <div class=""logoControl""><label>左右偏移</label><input id=""logoXRange"" type=""range"" min=""-500"" max=""500"" step=""1"" oninput=""logoSliderChanged()""><input id=""logoXValue"" type=""number"" oninput=""logoNumberChanged()""></div>
        <div class=""logoControl""><label>上下偏移</label><input id=""logoYRange"" type=""range"" min=""-500"" max=""500"" step=""1"" oninput=""logoSliderChanged()""><input id=""logoYValue"" type=""number"" oninput=""logoNumberChanged()""></div>
        <div class=""logoControl""><label>缩放倍率</label><input id=""logoScaleRange"" type=""range"" min=""0.1"" max=""5"" step=""0.05"" oninput=""logoSliderChanged()""><input id=""logoScaleValue"" type=""number"" min=""0.05"" step=""0.05"" oninput=""logoNumberChanged()""></div>
        <div class=""hint"" id=""logoResolution"">图片原始分辨率：读取中</div>
      </div>
      <div>
        <h2>实时预览</h2>
        <div class=""logoPreview""><img id=""logoPreviewImg"" class=""logoPreviewImg"" src=""/api/notice-logo"" alt=""logo preview""></div>
      </div>
    </section>
  </section>
  <div class=""credit"">Vibe code By Neko_qmq</div>
</main>
<script>
const nowEl = document.getElementById('now');
const listEl = document.getElementById('list');
const unresolvedEl = document.getElementById('unresolved');
const editValues = {};
let settingsLoaded = false;
function esc(s){return String(s??'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/""/g,'&quot;').replace(/'/g,'&#39;');}
async function api(url){const r=await fetch(url,{cache:'no-store'}); return await r.json();}
function showTab(name){
  document.getElementById('queueTab').classList.toggle('hidden', name !== 'queue');
  document.getElementById('settingsTab').classList.toggle('hidden', name !== 'settings');
  document.getElementById('logoTab').classList.toggle('hidden', name !== 'logo');
  document.getElementById('tabQueueBtn').classList.toggle('active', name === 'queue');
  document.getElementById('tabSettingsBtn').classList.toggle('active', name === 'settings');
  document.getElementById('tabLogoBtn').classList.toggle('active', name === 'logo');
  if(name === 'settings' && !settingsLoaded) loadSettings();
  if(name === 'logo') {
    const ready = settingsLoaded ? Promise.resolve() : loadSettings();
    ready.then(syncLogoControls);
  }
}
async function add(){const q=document.getElementById('q').value.trim(); if(!q)return; const r=await api('/api/request?user=admin&q='+encodeURIComponent(q)); if(!r.success && r.message!=='song_unresolved') alert(r.message); document.getElementById('q').value=''; tick();}
async function selectNext(){const r=await api('/api/admin/select-next'); if(!r.success) alert(r.message); tick();}
async function remove(id){await api('/api/admin/remove?id='+id); delete editValues[id]; tick();}
async function move(id,dir){await api('/api/admin/move?id='+id+'&dir='+dir); tick();}
function saveEdit(id,value){editValues[id]=value;}
async function resolveReq(id){const el=document.getElementById('edit_'+id); const q=el ? el.value.trim() : ''; if(!q)return; const r=await api('/api/admin/resolve?id='+id+'&q='+encodeURIComponent(q)); if(r.success) delete editValues[id]; else alert(r.message); tick();}
function editBox(r){const v=Object.prototype.hasOwnProperty.call(editValues,r.requestId)?editValues[r.requestId]:r.query; return `<input class=""edit"" id=""edit_${r.requestId}"" value=""${esc(v)}"" oninput=""saveEdit(${r.requestId},this.value)"" placeholder=""改为曲名/别名/ID""><button class=""fix"" onclick=""resolveReq(${r.requestId})"">应用</button>`;}
function card(r,i){return `<div class=""row""><div><div class=""name"">${i+1}. ${esc(r.music.name)}</div><div class=""meta"">#${r.music.id} / ${esc(r.userName||'观众')} / original: ${esc(r.query)}</div></div><div class=""actions"">${editBox(r)}<button onclick=""move(${r.requestId},'up')"">上移</button><button onclick=""move(${r.requestId},'down')"">下移</button><button class=""warn"" onclick=""remove(${r.requestId})"">删除</button></div></div>`;}
function unresolvedCard(r,i){return `<div class=""row""><div><div class=""name"">${i+1}. ${esc(r.query)}</div><div class=""meta"">${esc(r.userName||'观众')} 的点歌未能可靠识别，请手动改为正式曲名、别名或 ID</div></div><div class=""actions"">${editBox(r)}<button class=""warn"" onclick=""remove(${r.requestId})"">删除</button></div></div>`;}
function isEditingRequest(){const el=document.activeElement; return !!(el && el.classList && el.classList.contains('edit'));}
async function tick(){
  if(isEditingRequest()) return;
  const data = await api('/api/queue');
  const playing = data.lastSelected;
  nowEl.innerHTML = playing ? `<h2>正在游玩</h2><div class=""now""><div><div class=""name"">${esc(playing.music.name)}</div><div class=""meta"">#${playing.music.id} / ${esc(playing.userName||'观众')}</div></div><div></div></div>` : '';
  const queue = data.queue || [];
  const unresolved = data.unresolved || [];
  listEl.innerHTML = '<h2>已识别点歌队列</h2>' + (queue.length ? queue.map(card).join('') : '<div class=""empty"">队列为空</div>');
  unresolvedEl.innerHTML = '<h2>无法识别的点歌单</h2>' + (unresolved.length ? unresolved.map(unresolvedCard).join('') : '<div class=""empty"">暂无无法识别的点歌</div>');
}
function status(text){document.getElementById('settingsStatus').textContent = text;}
async function loadSettings(){
  const data = await api('/api/admin/settings');
  document.getElementById('aliasesText').value = data.aliases || '';
  document.getElementById('blacklistText').value = data.blacklist || '';
  document.getElementById('noticeTextArea').value = data.notice || '';
  document.getElementById('autoSelectCheck').checked = !!data.autoSelect;
  settingsLoaded = true;
  syncLogoControls();
  status('settings loaded');
}
async function saveSettings(){
  const body = new URLSearchParams({
    aliases: document.getElementById('aliasesText').value,
    blacklist: document.getElementById('blacklistText').value,
    notice: document.getElementById('noticeTextArea').value,
    autoSelect: document.getElementById('autoSelectCheck').checked ? 'true' : 'false'
  });
  const res = await fetch('/api/admin/settings', { method: 'POST', body });
  const data = await res.json();
  status(data.success ? 'settings saved and applied' : ('save failed: ' + data.message));
}
async function reloadConfig(){const data = await api('/api/admin/reload-config'); status(data.success ? 'config reloaded' : ('reload failed: ' + data.message));}
async function refreshCatalog(){const data = await api('/api/refresh'); status('catalog refreshed: ' + (data.catalogCount ?? 0) + ' songs');}
const logoDefaults = { logoWidth: 172, logoHeight: 156, logoX: 0, logoY: 0, logoScale: 1 };
const logoKeys = Object.keys(logoDefaults);
function noticeText(){return document.getElementById('noticeTextArea').value || '';}
function setNoticeText(text){document.getElementById('noticeTextArea').value = text;}
function readLogoValue(key){
  const match = noticeText().match(new RegExp('^' + key + '\\s*=\\s*([^\\r\\n]+)', 'im'));
  const value = match ? Number(match[1].trim()) : NaN;
  return Number.isFinite(value) ? value : logoDefaults[key];
}
function writeLogoValue(key,value){
  let text = noticeText();
  const line = key + '=' + value;
  const re = new RegExp('^' + key + '\\s*=.*$', 'im');
  text = re.test(text) ? text.replace(re, line) : (text.replace(/\s*$/, '') + '\\r\\n' + line + '\\r\\n');
  setNoticeText(text);
}
function setLogoInputs(key,value){
  document.getElementById(key + 'Range').value = value;
  document.getElementById(key + 'Value').value = value;
}
function syncLogoControls(){
  for(const key of logoKeys) setLogoInputs(key, readLogoValue(key));
  updateLogoPreview();
}
function logoSliderChanged(){
  for(const key of logoKeys) document.getElementById(key + 'Value').value = document.getElementById(key + 'Range').value;
  commitLogoControls();
}
function logoNumberChanged(){
  for(const key of logoKeys) document.getElementById(key + 'Range').value = document.getElementById(key + 'Value').value;
  commitLogoControls();
}
function commitLogoControls(){
  for(const key of logoKeys) writeLogoValue(key, document.getElementById(key + 'Value').value || logoDefaults[key]);
  updateLogoPreview();
}
function resetLogoControls(){
  for(const key of logoKeys) setLogoInputs(key, logoDefaults[key]);
  commitLogoControls();
}
function updateLogoPreview(){
  const img = document.getElementById('logoPreviewImg');
  if(!img) return;
  img.style.setProperty('--lw', Math.max(1, Number(document.getElementById('logoWidthValue').value || 172)) + 'px');
  img.style.setProperty('--lh', Math.max(1, Number(document.getElementById('logoHeightValue').value || 156)) + 'px');
  img.style.setProperty('--lx', Number(document.getElementById('logoXValue').value || 0) + 'px');
  img.style.setProperty('--ly', Number(document.getElementById('logoYValue').value || 0) + 'px');
  img.style.setProperty('--ls', Math.max(.05, Number(document.getElementById('logoScaleValue').value || 1)));
}
document.getElementById('logoPreviewImg').onload = function(){
  document.getElementById('logoResolution').textContent = 'source resolution: ' + this.naturalWidth + ' x ' + this.naturalHeight;
};
tick(); setInterval(tick, 1500);
</script>
</body>
</html>";
        }

        private static string AdminHtml()
        {
            if (DateTime.UtcNow.Year > 0)
            {
                return AdminHtmlV2();
            }

            if (DateTime.UtcNow.Year > 0)
            {
                return @"<!doctype html>
<html lang=""zh-CN"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width,initial-scale=1"">
  <title>maimai 点歌管理</title>
  <style>
    body { margin: 0; background: #101719; color: #f3f6ef; font-family: ""Microsoft YaHei UI"", sans-serif; }
    main { max-width: 1080px; margin: 0 auto; padding: 24px; }
    h1 { margin: 0 0 16px; font-size: 28px; }
    h2 { margin: 24px 0 12px; }
    .bar { display: flex; gap: 8px; margin-bottom: 16px; }
    input { flex: 1; padding: 10px 12px; border: 1px solid #405057; border-radius: 8px; background: #192326; color: #fff; }
    button { padding: 8px 12px; border: 0; border-radius: 8px; background: #6ee7f2; color: #071013; cursor: pointer; font-weight: 700; }
    button.warn { background: #ff7f6e; }
    button.fix { background: #ffb15c; }
    button.gray { background: #46555b; color: #fff; }
    .box { padding: 14px; margin-top: 16px; border: 1px solid rgba(255,255,255,.12); border-radius: 14px; background: #141f22; }
    .unresolvedBox { border-color: rgba(255,107,53,.78); background: linear-gradient(145deg, rgba(72,31,17,.92), rgba(32,20,47,.88)); }
    .now, .row { display: grid; grid-template-columns: 1fr auto; gap: 12px; align-items: center; padding: 12px; margin-bottom: 10px; border: 1px solid rgba(255,255,255,.12); border-radius: 10px; background: #182225; }
    .unresolvedBox .row { border-color: rgba(255,177,92,.45); background: rgba(255,177,92,.08); }
    .now { border-color: #ffdc5e; background: #272414; }
    .name { font-size: 18px; font-weight: 700; }
    .meta { color: #aeb8b4; margin-top: 4px; }
    .actions { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
    .edit { width: 220px; flex: 0 0 220px; }
    .empty { color: #aeb8b4; padding: 24px; border: 1px dashed rgba(255,255,255,.18); border-radius: 10px; }
  </style>
</head>
<body>
<main>
  <h1>maimai 点歌管理</h1>
  <div class=""bar"">
    <input id=""q"" placeholder=""手动点歌：曲名、别名或 ID"">
    <button onclick=""add()"">加入队列</button>
    <button onclick=""selectNext()"">切到下一首</button>
    <button class=""gray"" onclick=""tick()"">刷新</button>
  </div>
  <section id=""now""></section>
  <section class=""box"" id=""list""></section>
  <section class=""box unresolvedBox"" id=""unresolved""></section>
</main>
<script>
const nowEl = document.getElementById('now');
const listEl = document.getElementById('list');
const unresolvedEl = document.getElementById('unresolved');
const editValues = {};
function esc(s){return String(s??'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/""/g,'&quot;').replace(/'/g,'&#39;');}
async function api(url){const r=await fetch(url,{cache:'no-store'}); return await r.json();}
async function add(){const q=document.getElementById('q').value.trim(); if(!q)return; const r=await api('/api/request?user=admin&q='+encodeURIComponent(q)); if(!r.success && r.message!=='song_unresolved') alert(r.message); document.getElementById('q').value=''; tick();}
async function selectNext(){const r=await api('/api/admin/select-next'); if(!r.success) alert(r.message); tick();}
async function remove(id){await api('/api/admin/remove?id='+id); tick();}
async function move(id,dir){await api('/api/admin/move?id='+id+'&dir='+dir); tick();}
function saveEdit(id,value){editValues[id]=value;}
async function resolveReq(id){const el=document.getElementById('edit_'+id); const q=el ? el.value.trim() : ''; if(!q)return; const r=await api('/api/admin/resolve?id='+id+'&q='+encodeURIComponent(q)); if(r.success) delete editValues[id]; else alert(r.message); tick();}
function editBox(r){const v=Object.prototype.hasOwnProperty.call(editValues,r.requestId)?editValues[r.requestId]:r.query; return `<input class=""edit"" id=""edit_${r.requestId}"" value=""${esc(v)}"" oninput=""saveEdit(${r.requestId},this.value)"" placeholder=""改为曲名/别名/ID""><button class=""fix"" onclick=""resolveReq(${r.requestId})"">应用</button>`;}
function card(r,i){return `<div class=""row""><div><div class=""name"">${i+1}. ${esc(r.music.name)}</div><div class=""meta"">#${r.music.id} / ${esc(r.userName||'观众')} / 原文：${esc(r.query)}</div></div><div class=""actions"">${editBox(r)}<button onclick=""move(${r.requestId},'up')"">上移</button><button onclick=""move(${r.requestId},'down')"">下移</button><button class=""warn"" onclick=""remove(${r.requestId})"">删除</button></div></div>`;}
function unresolvedCard(r,i){return `<div class=""row""><div><div class=""name"">${i+1}. ${esc(r.query)}</div><div class=""meta"">${esc(r.userName||'观众')} 的点歌未能可靠识别，请手动改为正式曲名、别名或 ID</div></div><div class=""actions"">${editBox(r)}<button class=""warn"" onclick=""remove(${r.requestId})"">删除</button></div></div>`;}
async function tick(){
  const data = await api('/api/queue');
  const playing = data.lastSelected;
  nowEl.innerHTML = playing ? `<h2>正在游玩</h2><div class=""now""><div><div class=""name"">${esc(playing.music.name)}</div><div class=""meta"">#${playing.music.id} / ${esc(playing.userName||'观众')}</div></div><div></div></div>` : '';
  const queue = data.queue || [];
  const unresolved = data.unresolved || [];
  listEl.innerHTML = '<h2>已识别点歌队列</h2>' + (queue.length ? queue.map(card).join('') : '<div class=""empty"">队列为空</div>');
  unresolvedEl.innerHTML = '<h2>无法识别的点歌单</h2>' + (unresolved.length ? unresolved.map(unresolvedCard).join('') : '<div class=""empty"">暂无无法识别的点歌</div>');
}
tick(); setInterval(tick, 1500);
</script>
</body>
</html>";
            }

            return @"<!doctype html>
<html lang=""zh-CN"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width,initial-scale=1"">
  <title>maimai 点歌管理</title>
  <style>
    body { margin: 0; background: #101719; color: #f3f6ef; font-family: ""Microsoft YaHei UI"", sans-serif; }
    main { max-width: 960px; margin: 0 auto; padding: 24px; }
    h1 { margin: 0 0 16px; font-size: 28px; }
    .bar { display: flex; gap: 8px; margin-bottom: 16px; }
    input { flex: 1; padding: 10px 12px; border: 1px solid #405057; border-radius: 8px; background: #192326; color: #fff; }
    button { padding: 8px 12px; border: 0; border-radius: 8px; background: #6ee7f2; color: #071013; cursor: pointer; font-weight: 700; }
    button.warn { background: #ff7f6e; }
    button.gray { background: #46555b; color: #fff; }
    .now, .row { display: grid; grid-template-columns: 1fr auto; gap: 12px; align-items: center; padding: 12px; margin-bottom: 10px; border: 1px solid rgba(255,255,255,.12); border-radius: 10px; background: #182225; }
    .now { border-color: #ffdc5e; background: #272414; }
    .name { font-size: 18px; font-weight: 700; }
    .meta { color: #aeb8b4; margin-top: 4px; }
    .actions { display: flex; gap: 6px; }
    .empty { color: #aeb8b4; padding: 24px; border: 1px dashed rgba(255,255,255,.18); border-radius: 10px; }
  </style>
</head>
<body>
<main>
  <h1>maimai 点歌管理</h1>
  <div class=""bar"">
    <input id=""q"" placeholder=""本地测试点歌：曲名、别名或 ID"">
    <button onclick=""add()"">加入队列</button>
    <button onclick=""selectNext()"">切到下一首</button>
    <button class=""gray"" onclick=""tick()"">刷新</button>
  </div>
  <section id=""now""></section>
  <section id=""list""></section>
</main>
<script>
const nowEl = document.getElementById('now');
const listEl = document.getElementById('list');
function esc(s){return String(s??'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/""/g,'&quot;').replace(/'/g,'&#39;');}
async function api(url){const r=await fetch(url,{cache:'no-store'}); return await r.json();}
async function add(){const q=document.getElementById('q').value.trim(); if(!q)return; await api('/api/request?user=admin&q='+encodeURIComponent(q)); document.getElementById('q').value=''; tick();}
async function selectNext(){const r=await api('/api/admin/select-next'); if(!r.success) alert(r.message); tick();}
async function remove(id){await api('/api/admin/remove?id='+id); tick();}
async function move(id,dir){await api('/api/admin/move?id='+id+'&dir='+dir); tick();}
function card(r,i){return `<div class=""row""><div><div class=""name"">${i+1}. ${esc(r.music.name)}</div><div class=""meta"">#${r.music.id} / ${esc(r.userName||'观众')} / ${esc(r.query)}</div></div><div class=""actions""><button onclick=""move(${r.requestId},'up')"">上移</button><button onclick=""move(${r.requestId},'down')"">下移</button><button class=""warn"" onclick=""remove(${r.requestId})"">删除</button></div></div>`;}
async function tick(){
  const data = await api('/api/queue');
  const playing = data.lastSelected;
  nowEl.innerHTML = playing ? `<h2>正在游玩</h2><div class=""now""><div><div class=""name"">${esc(playing.music.name)}</div><div class=""meta"">#${playing.music.id} / ${esc(playing.userName||'观众')}</div></div><div></div></div>` : '';
  const queue = data.queue || [];
  listEl.innerHTML = queue.length ? '<h2>等待队列</h2>'+queue.map(card).join('') : '<div class=""empty"">队列为空</div>';
}
tick(); setInterval(tick, 1500);
</script>
</body>
</html>";
        }

        private void EnsureHtml()
        {
            if (File.Exists(_htmlPath))
            {
                EnsureNotice();
                return;
            }
            File.WriteAllText(_htmlPath, BoardHtml.DefaultHtml, Encoding.UTF8);
            EnsureNotice();
            MelonLogger.Msg("Created editable request board html: " + _htmlPath);
        }

        private void EnsureNotice()
        {
            var path = NoticePath();
            if (File.Exists(path))
            {
                return;
            }

            File.WriteAllText(path,
                "# \u76f4\u64ad\u770b\u677f\u201c\u6b63\u5728\u64ad\u653e\u201d\u4e0b\u65b9\u63d0\u793a\u914d\u7f6e\r\n"
                + "# flashSeconds \u63a7\u5236\u63d0\u793a\u95ea\u70c1\u591a\u4e45\u540e\u5207\u6362\u5230\u672c\u5730 logo\r\n"
                + "# logo \u6587\u4ef6\u653e\u5728 Mods \u540c\u76ee\u5f55\uff0c\u6587\u4ef6\u540d\u4f7f\u7528 MaimaiLiveRequestLogo.png/jpg/jpeg/webp/gif/svg\r\n"
                + "flashSeconds=5\r\n"
                + "logoWidth=172\r\n"
                + "logoHeight=156\r\n"
                + "logoX=0\r\n"
                + "logoY=0\r\n"
                + "logoScale=1\r\n"
                + "line=\u6309\u987a\u5e8f\u6253\u6b4c\r\n"
                + "line=\u53ef\u80fd\u4e0d\u6253\u5927\u6b4c\r\n"
                + "line=\u70b9\u6b4c\u65b9\u5f0f\uff1a\u70b9\u6b4c \u66f2\u540d/\u522b\u540d/ID\r\n"
                + "line=\u4f8b\u5982\uff1a\u70b9\u6b4c \u7687\u5e1d\r\n",
                Encoding.UTF8);
            if (File.Exists(path))
            {
                return;
            }

            File.WriteAllText(path,
                "# 直播看板右下角提示配置\r\n"
                + "# flashSeconds 控制提示闪烁多久后切换到本地 logo\r\n"
                + "# logo 文件放在 Mods 同目录，文件名使用 MaimaiLiveRequestLogo.png/jpg/webp/gif/svg\r\n"
                + "flashSeconds=5\r\n"
                + "line=按顺序打歌\r\n"
                + "line=可能不打大歌\r\n"
                + "line=点歌方式：点歌 曲名/别名/ID\r\n"
                + "line=例如：点歌 皇帝\r\n",
                Encoding.UTF8);
        }

        private string NoticePath()
        {
            return Path.Combine(ModDirectory(), "MaimaiLiveRequestBoardNotice.txt");
        }

        private string AliasPath()
        {
            return Path.Combine(ModDirectory(), "MaimaiSongAliases.txt");
        }

        private string BlacklistPath()
        {
            return Path.Combine(ModDirectory(), "MaimaiSongBlacklist.txt");
        }

        private string ModDirectory()
        {
            return Path.GetDirectoryName(_htmlPath) ?? Environment.CurrentDirectory;
        }
    }
}
