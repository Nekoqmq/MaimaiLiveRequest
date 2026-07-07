# MaimaiLiveRequestMod 安装与使用说明

## 截图
![示例图片1]([https://example.com/image.png](https://github.com/Nekoqmq/MaimaiLiveRequest/imgs/1.png) "图片1")
![示例图片2]([https://example.com/image.png](https://github.com/Nekoqmq/MaimaiLiveRequest/imgs/2.png) "图片2")
![示例图片3]([https://example.com/image.png](https://github.com/Nekoqmq/MaimaiLiveRequest/imgs/3.png) "图片3")
## 引言

这是基于MelonLoader的maimai 直播间点歌 模组，可读取本地游戏曲库，支持本地点歌接口、B站弹幕点歌、`*可使用别名模糊点歌`、防重复点歌 、歌曲黑名单、自动切歌、管理员手动切歌和 OBS 点歌看板。

本模组的 B 站弹幕点歌通过适配 [xfgryujk/blivechat](https://github.com/xfgryujk/blivechat) 的本地 WebSocket API 实现。

## 安装

把最新的 `MaimaiLiveRequest.zip` 解压，并将里面的这些文件放进游戏目录的 `Mods` 文件夹：

```text
MaimaiLiveRequestMod.dll
MaimaiLiveRequestBoard.html
MaimaiLiveRequestBoardNotice.txt
MaimaiSongAliases.txt
MaimaiSongBlacklist.txt
```

如果之前安装过旧版本，请覆盖 `MaimaiLiveRequestBoard.html`。模组不会自动覆盖已有 HTML，否则你修改过的看板会被覆盖。

## 浏览器地址

默认端口是 `8890`：

```text
OBS 点歌看板：http://127.0.0.1:8890/board
管理员页面：http://127.0.0.1:8890/admin
状态：http://127.0.0.1:8890/api/health
```

本地点歌测试：

```text
http://127.0.0.1:8890/api/request?user=test&q=True%20Love%20Song
http://127.0.0.1:8890/api/request?user=test&q=8
```

在管理员页面中可使用添加、删除、上移、下移、“切到下一首”、改歌名、“设置”和“Logo”选项卡等功能，可用于快速调整看板及点歌队列。黑名单中的曲目无法点歌，可以在“设置”里可以打开自动切歌，在进入选歌界面时

## OBS 看板
```text
左侧：点歌队列，第一首作为 NEXT SONG 
右上：正在游玩曲目 NOW PLAYING
右下：独立颜色的点歌说明框，淡入淡出显示约 5 秒后切换到图片，图片停留同样时长后自动切回说明
中间：留空，用于放机台画面和游戏画面
```

点歌说明框读取 `Mods/MaimaiLiveRequestBoardNotice.txt`：

```text
flashSeconds=5
line=按顺序打歌
line=点歌方式：点歌 曲名/别名/ID
line=例如：点歌 皇帝
```

logo 微调参数也写在同一个文件：

```text
logoWidth=172
logoHeight=156
logoX=0
logoY=0
logoScale=1
```

`logoWidth/logoHeight` 是展示分辨率，`logoX/logoY` 是相对右下角的偏移，`logoScale` 是缩放倍率。可以在管理员页面“Logo”选项卡用滑条实时预览并保存。

想显示主播logo或自定义图片，把图片放到 `Mods` 目录，文件名使用：

```text
MaimaiLiveRequestLogo.png
MaimaiLiveRequestLogo.jpg
MaimaiLiveRequestLogo.jpeg
MaimaiLiveRequestLogo.webp
MaimaiLiveRequestLogo.gif
MaimaiLiveRequestLogo.svg
```

## 弹幕点歌配置

MelonLoader 配置文件在游戏根目录的 `UserData\MelonPreferences.cfg`，例如：

```text
\Package\UserData\MelonPreferences.cfg
```

如果文件不存在，先启动一次游戏让 MelonLoader 自动生成。
在配置文件中添加以下内容：

```toml
[MaimaiLiveRequest]
OverlayPrefix = "http://127.0.0.1:8890/"
DanmakuSource = "blivechat"
BlivechatUrl = "ws://127.0.0.1:12450/api/chat"
BlivechatRoomCode = ""
BlivechatRoomId = 0
BiliRoomId = 0
Command = "\u70b9\u6b4c"
AutoSelect = true
AutoSelectDelaySeconds = 1
MaxQueue = 30
Debug = false
```

部署 blivechat，并完成配置。
`BlivechatRoomCode` 填写你的身份码到双引号内（自行寻找）， `BlivechatRoomId`填写你的房间id（直播间后的数字）。

观众弹幕格式：

```text
点歌 True Love Song  （全名）
点歌 皇帝  （别名）
点歌 8  （曲目id）
```

## 别名和黑名单
`*可使用别名模糊点歌`：曲目需要在别名表中才能被识别

`MaimaiSongAliases.txt` 一行一个别名映射：
MaimaiSongAliases.txt中已填写了一定的别名，可自行补充、修改、删除。

```text
别名=正式曲名
```

`MaimaiSongBlacklist.txt` 按歌曲 ID 禁点，一行一个：

```text
# 井号开头是注释
1794
8
```

管理员页面“设置”选项卡可以直接编辑并保存 `MaimaiSongAliases.txt`、`MaimaiSongBlacklist.txt` 和 `MaimaiLiveRequestBoardNotice.txt`。保存后会立即应用到点歌；“刷新曲库”用于加载新添加的歌曲。

若修改后存在异常，建议重启游戏

## 未识别点歌

观众发送未写进别名表、也不能可靠匹配到正式曲名的点歌时，点歌不会直接加入正式队列，而是放进管理员页面下方的“无法识别的点歌单”。

管理员可以在该列表里输入正确曲名、别名或 ID，点击“应用”后重新识别并加入正式队列。已识别队列里的项目也可以用同样方式修改。

## 常见检查

打开：

```text
http://127.0.0.1:8890/api/health
```

正常情况应看到：

```text
version = 0.2.30
catalogCount 大于 0
musicSelectReady = true
```

如果 `catalogCount = 0`，先进入一次选歌界面，再刷新 `/api/health`。
已使用SDEZ1.60完成功能测试

## 免责声明

本软件为非官方maimai DX 直播点歌辅助模组，**仅供个人直播、录屏、本地测试与技术研究使用**。

本软件提供本地网页看板、管理员面板、弹幕点歌队列、曲库读取、别名匹配以及选歌辅助等功能。它**不应**用于商业街机运营、破坏游戏正常体验、绕过付费或解锁机制、干扰联网服务、伪造成绩、修改分数、修改存档、作弊或任何违反当地法律法规、游戏服务条款、场地规则的用途。

使用 MelonLoader、第三方模组或本地自动化功能可能导致游戏异常、崩溃、数据丢失、配置损坏、账号或设备使用风险。使用者应自行确认使用环境、授权范围和合规性，并自行承担全部后果。**作者不对任何直接或间接损失负责**。

本项目与 **SEGA**、maimai / maimai DX 官方及其关联公司**无关**，亦未获得其认可、授权、赞助或支持。所有游戏名称、曲名、商标、版权内容均归其各自权利人所有。

本项目可能包含由 AI 辅助生成、整理或修改的代码、文档、界面文本和配置内容。

下载、安装、运行或分发本软件，即表示你已阅读、理解并同意以上免责声明，并愿意承担可能存在的法律风险。

