# 兰桥协议 v1

电脑是唯一的 HTTP / WebSocket 服务端。手机只做客户端：发现电脑、配对、用 WebSocket 传控制消息，用 HTTPS 传文件字节。没有云、没有账号。

默认端口 `47821`。mDNS 服务类型 `_lanbridge._tcp`。只使用 HTTP/1.1（WebSocket 升级需要它）。

## 信封

WebSocket 文本帧是一条 JSON：

```json
{"v":1,"type":"hello","msgId":"<uuid>","ts":1700000000000,"payload":{}}
```

`v` 必须是 1。属性名用 camelCase。未知的 `type` 忽略。单条消息不超过 512 KiB。

## 发现

电脑向 `224.0.0.251:5353` 宣告 PTR / SRV / TXT / A。实例名由电脑名称和设备 id 生成，主机名形如 `lanbridge-<id 前 12 位>.local`。

TXT 键：

| 键 | 含义 |
| --- | --- |
| id | 电脑设备 id（UUID） |
| name | 显示名，可能被截短 |
| ver | 协议版本，`1` |
| fp | 证书 SHA-256，64 位小写十六进制 |
| role | `pc` |
| port | 监听端口 |

手机用系统 NSD 浏览 `_lanbridge._tcp`。二维码不依赖 mDNS，链接里已经带了地址。

## 身份和请求签名

电脑持有一张自签 TLS 证书。指纹是证书 DER（`RawData` / `encoded`）的 SHA-256，小写十六进制。手机只信任这个指纹，不按主机名校验。

手机持有一对 P-256（secp256r1）密钥。配对成功后，电脑保存公钥的 SPKI。之后每条 HTTPS 请求，包括带身份的 WebSocket 升级，都要签名。

待签名字符串，用换行拼接，没有末尾换行：

```text
timestamp
nonce
METHOD
path
contentSha256
```

- `timestamp`：Unix 毫秒。允许偏差 ±300000 ms。
- `nonce`：16 到 64 位小写十六进制。同一设备 10 分钟内不可重复。
- `METHOD`：大写。
- `path`：不含查询串，例如 `/v1/files/{id}`。
- `contentSha256`：请求体 SHA-256 小写十六进制；没有请求体时为空字符串。

签名是 ECDSA SHA-256，DER 编码（Java `SHA256withECDSA`，.NET `DSASignatureFormat.Rfc3279DerSequence`），Base64 放在头里。DER 长度必须在 64 到 120 字节之间。

请求头：

- `X-LanBridge-Device-Id`
- `X-LanBridge-Timestamp`
- `X-LanBridge-Nonce`
- `X-LanBridge-Signature`
- `X-LanBridge-Content-Sha256`（有请求体时）

来源地址不是局域网（回环、RFC1918、链路本地、IPv6 ULA / 链路本地）则直接 403。未配对设备不能访问文件和剪贴板接口。`GET /v1/info` 是唯一不需要身份的 HTTP 接口。

## 配对

电脑打开 120 秒窗口，生成 6 位数字 PIN。连续 5 次错误后锁定。比较是常量时间。

二维码内容：

```text
lanbridge://pair?v=1&host=192.168.1.20&port=47821&id=<电脑 id>&fp=<指纹>&pin=042813&exp=<unix 秒>
```

配对进行时，未签名的 WebSocket 可以连上，而且只接受 `pair.request`。请求载荷：

```json
{"deviceId":"<手机 uuid>","deviceName":"Mate 60","platform":"android","publicKeySpki":"<base64>","pin":"042813","protocol":1}
```

PIN 正确后窗口关闭，电脑发送 `pair.pending`，并在 60 秒内等用户确认。用户拒绝或超时则 `pair.reject`，`reason` 为 `denied` 或 `timeout`。PIN 错误时 `reason` 为 `mismatch`，连接保持，可以再试。确认后发送 `pair.accept`（电脑 id、名称、指纹、协议版本）和 `hello`。同一时间只有一个配对窗口、一个正在配对的设备。

`pair.reject` 的其他原因：`bad_request`、`bad_key`、`bad_version`、`locked`、`closed`。

信任可以撤销。电脑发送 `device.revoked` 后关闭连接。手机也可以发送 `device.unlink`。

## 文件

字节始终经过电脑上的 HTTP 服务。

手机发给电脑：

1. WebSocket `file.offer`，`direction` 为 `to_pc`。
2. 电脑回复 `file.accept`（`mode` 为 `put`）、`file.pending`（等用户确认）或 `file.reject`。
3. 整文件 `PUT /v1/files/{id}`，`Content-Sha256` 必须等于 offer 里的整文件哈希。
4. 或者按顺序 `PUT /v1/files/{id}/chunks/{index}`。块大小 1048576，偏移是 `index * 块大小`。`Content-Sha256` 是这一块的哈希。重复提交刚写过的最后一块视为成功；偏移对不上返回 409 `offset_conflict`。
5. 电脑先写入 `.part`，算 SHA-256，一致后改名为最终文件。

电脑发给手机：`file.offer` 的 `direction` 为 `to_phone`。手机 `GET /v1/files/{id}`，校验哈希后自己保存，再发 `file.complete` 或 `file.error`。

`GET /v1/files/{id}/status` 返回已收字节和状态。V1 实现了顺序补块，不提供“从断点继续”的界面。那是下一阶段。

拒绝原因：`invalid_size`、`too_large`（默认上限 2 GiB，硬上限 8 GiB）、`busy`（同时最多 2 个）、`no_space`（文件大小再加 64 MiB 余量）、`user_declined`、`bad_name`、`bad_hash`、`hash_mismatch`。

文件名只保留最后一段，去掉控制字符、`<>:"/\|?*` 和双向控制符，去掉首尾空格和点。保留设备名（`CON`、`PRN`、`AUX`、`NUL`、`COM1`–`COM9`、`LPT1`–`LPT9`）加 `_` 前缀。最长 120 个 UTF-16 码元。空名变成 `received.bin`。重名写成 `name (n).ext`。收到的文件不会被执行。

## 剪贴板

只同步文字。图片留给下一版。

发送前把 CRLF / CR 归一成 LF，去掉 NUL，再算 SHA-256。超过 256 KiB 拒绝。

`clipboard.push` 载荷：`text`、`sha256`、`manual`。接收方回 `clipboard.ack`：`refId` 是原消息的 `msgId`，`ok`，可选 `reason`。

自动同步默认关闭。手动发送时 `manual` 为 true。敏感内容过滤默认开启：自动路径不发送、也不接收；手动确认过的可以过。过滤命中私钥 PEM、`AKIA…`、`sk-`、`ghp_`、`xox*`，以及带标签的密码 / token / 密码 / 口令（值至少 4 个非空格字符）。不用银行卡 Luhn，避免把身份证号误判。敏感文字不写入历史。

回环保护：刚发出去的哈希，以及刚写入本机剪贴板后的 5 秒，不再自动回传。

## 保活

电脑每 25 秒发 `ping`，对端回 `pong`。连接断开后手机按 1、2、4… 秒重试，最多 30 秒。
