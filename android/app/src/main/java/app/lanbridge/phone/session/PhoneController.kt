package app.lanbridge.phone.session

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Environment
import android.os.Handler
import android.os.Looper
import android.os.StatFs
import android.provider.MediaStore
import android.util.Base64
import app.lanbridge.phone.data.AppDb
import app.lanbridge.phone.data.PeerEntity
import app.lanbridge.phone.data.Prefs
import app.lanbridge.phone.data.TransferEntity
import app.lanbridge.phone.net.DeviceIdentity
import app.lanbridge.phone.net.LanMdnsBrowser
import app.lanbridge.protocol.PairingUi
import app.lanbridge.protocol.Protocol
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.BufferedSink
import okio.source
import org.json.JSONObject
import java.io.File
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

class PhoneController(private val context: Context, private val notify: (String, String) -> Unit) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val prefs = Prefs(context)
    private val db = AppDb.get(context)
    private val identity = DeviceIdentity()
    private val stateFlow = MutableStateFlow(PhoneState())
    val state: StateFlow<PhoneState> = stateFlow

    private var http: OkHttpClient? = null
    private var socket: WebSocket? = null
    private var peer: PeerEntity? = null
    private var reconnectJob: Job? = null
    private var stopped = false
    private val pairing = AtomicBoolean(false)
    private var pairWaiter: CompletableDeferred<JSONObject>? = null
    private val fileWaiters = ConcurrentHashMap<String, CompletableDeferred<JSONObject>>()
    private val ackWaiters = ConcurrentHashMap<String, CompletableDeferred<JSONObject>>()
    @Volatile var textToApply: String? = null

    private var nsd: NsdManager? = null
    private var discovery: NsdManager.DiscoveryListener? = null
    private var multicast: WifiManager.MulticastLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private val mainHandler = Handler(Looper.getMainLooper())
    private val browser = LanMdnsBrowser(context, ::addFound) { message -> update { it.copy(detail = message) } }
    private var nsdRetried = false

    fun start() {
        stopped = false
        scope.launch {
            publishSettings()
            refreshHistory()
            peer = db.peers().current()
            if (peer == null) {
                update { it.copy(status = "未配对", detail = "点「配对电脑」，扫描电脑上的二维码，或在同一 Wi-Fi 下选择发现到的电脑。", online = false) }
            } else {
                scheduleReconnect(0)
            }
        }
    }

    fun stop() {
        stopped = true
        reconnectJob?.cancel()
        socket?.close(1000, "bye")
        socket = null
        stopDiscovery()
    }

    fun updateName(name: String) {
        prefs.deviceName = name.trim().ifEmpty { android.os.Build.MODEL }
        publishSettings()
    }

    fun updateAutoReceive(value: Boolean) {
        prefs.autoReceive = value
        publishSettings()
    }

    fun updateSaveHistory(value: Boolean) {
        prefs.saveHistory = value
        publishSettings()
    }

    fun updateFilter(value: Boolean) {
        prefs.filterSensitive = value
        publishSettings()
    }

    fun forget() {
        scope.launch {
            reconnectJob?.cancel()
            socket?.close(1000, "unlink")
            socket = null
            peer = null
            db.peers().clear()
            update { it.copy(status = "未配对", detail = "已解除这台电脑。需要时重新配对。", online = false, peerName = null, pendingFile = null) }
        }
    }

    fun clearHistory() {
        scope.launch {
            db.transfers().clear()
            refreshHistory()
        }
    }

    fun startDiscovery() {
        stopDiscovery()
        nsdRetried = false
        val held = holdMulticast()
        browser.start()
        val manager = context.getSystemService(NsdManager::class.java)
        if (manager == null) {
            update {
                it.copy(detail = if (held) "系统发现不可用，已改用组播搜索。也可以填写 IP:端口。" else "组播锁没有打开，请填写 IP:端口。")
            }
            return
        }
        nsd = manager
        val listener = object : NsdManager.DiscoveryListener {
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                if (errorCode == NsdManager.FAILURE_ALREADY_ACTIVE && !nsdRetried) {
                    nsdRetried = true
                    try { manager.stopServiceDiscovery(this) } catch (_: Exception) {}
                    mainHandler.postDelayed({
                        try { manager.discoverServices("${Protocol.SERVICE_TYPE}.", NsdManager.PROTOCOL_DNS_SD, this) }
                        catch (_: Exception) {}
                    }, 400)
                    return
                }
                update { it.copy(detail = "系统发现没有开始（$errorCode）。组播搜索仍在进行，也可以填写 IP:端口。") }
            }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
            override fun onDiscoveryStarted(serviceType: String) {
                update { current ->
                    if (current.discovered.isNotEmpty()) current
                    else current.copy(detail = if (held) "正在搜索 _lanbridge._tcp，组播锁已打开。" else "正在搜索，但组播锁没有打开。也可以填写 IP:端口。")
                }
            }
            override fun onDiscoveryStopped(serviceType: String) {}
            override fun onServiceLost(serviceInfo: NsdServiceInfo) {}
            override fun onServiceFound(serviceInfo: NsdServiceInfo) = resolve(manager, serviceInfo, 0)
        }
        discovery = listener
        try {
            manager.discoverServices("${Protocol.SERVICE_TYPE}.", NsdManager.PROTOCOL_DNS_SD, listener)
        } catch (ex: Exception) {
            update { it.copy(detail = "搜索电脑失败：" + (ex.message ?: "未知错误") + "。可以填写 IP:端口。") }
        }
    }

    fun stopDiscovery() {
        browser.stop()
        val manager = nsd
        val listener = discovery
        if (manager != null && listener != null) {
            try { manager.stopServiceDiscovery(listener) } catch (_: Exception) {}
        }
        discovery = null
        nsd = null
        releaseMulticast()
    }

    fun chooseDiscovered(pc: DiscoveredPc) {
        update { it.copy(pairTarget = pc, manualError = null, detail = "已选择「${pc.name}」（${pc.host}:${pc.port}）。输入 6 位配对码后点连接。") }
    }

    fun setManualEndpoint(raw: String) {
        val parsed = PairingUi.parseManualEndpoint(raw)
        update { current ->
            when {
                parsed != null -> current.copy(
                    pairTarget = DiscoveredPc("手动输入", parsed.host, parsed.port, "", ""),
                    manualError = null,
                    detail = "已选择 ${parsed.host}:${parsed.port}。输入 6 位配对码后点连接。"
                )
                raw.isNotBlank() -> current.copy(
                    pairTarget = current.pairTarget?.takeUnless { it.name == "手动输入" },
                    manualError = "pending"
                )
                current.pairTarget?.name == "手动输入" -> current.copy(
                    pairTarget = current.discovered.firstOrNull(),
                    manualError = null
                )
                else -> current.copy(manualError = null)
            }
        }
    }

    fun applyQr(raw: String) {
        val link = Protocol.parsePairLink(raw)
        if (link == null) {
            update { it.copy(detail = "这不是兰桥的配对二维码。") }
            return
        }
        if (link.expiresAtUnixSeconds < System.currentTimeMillis() / 1000) {
            update { it.copy(detail = "二维码已经过期，请在电脑上重新打开配对。") }
            return
        }
        update {
            it.copy(
                pairTarget = DiscoveredPc(link.host, link.host, link.port, link.deviceId, link.fingerprint),
                suggestedPin = link.pin,
                manualError = null,
                detail = "已从二维码读到 ${link.host}:${link.port}。核对 6 位配对码后点连接。"
            )
        }
    }

    private fun resolve(manager: NsdManager, serviceInfo: NsdServiceInfo, attempt: Int) {
        try {
            @Suppress("DEPRECATION")
            manager.resolveService(serviceInfo, object : NsdManager.ResolveListener {
                override fun onResolveFailed(service: NsdServiceInfo, errorCode: Int) {
                    if (attempt < 3) mainHandler.postDelayed({ resolve(manager, service, attempt + 1) }, 350L * (attempt + 1))
                }
                override fun onServiceResolved(info: NsdServiceInfo) = acceptResolved(info)
            })
        } catch (_: Exception) {
            if (attempt < 3) mainHandler.postDelayed({ resolve(manager, serviceInfo, attempt + 1) }, 350L * (attempt + 1))
        }
    }

    private fun acceptResolved(info: NsdServiceInfo) {
        @Suppress("DEPRECATION")
        val host = info.host?.hostAddress ?: return
        if (host.contains(':')) return
        val attributes = info.attributes ?: emptyMap()
        fun txt(key: String) = attributes[key]?.toString(Charsets.UTF_8).orEmpty()
        val port = txt("port").toIntOrNull() ?: info.port.takeIf { it > 0 } ?: Protocol.DEFAULT_PORT
        addFound(DiscoveredPc(txt("name").ifBlank { info.serviceName ?: host }, host, port, txt("id"), txt("fp").lowercase()))
    }

    private fun addFound(found: DiscoveredPc) {
        if (found.host.isBlank() || found.port !in 1..65535) return
        update { current ->
            val known = current.discovered.any { it.host == found.host && it.port == found.port && it.fingerprint == found.fingerprint }
            val list = (current.discovered.filterNot {
                (found.fingerprint.length == 64 && it.fingerprint == found.fingerprint) || (it.host == found.host && it.port == found.port)
            } + found).take(8)
            val manual = current.pairTarget?.name == "手动输入"
            val target = when {
                manual -> current.pairTarget
                current.pairTarget == null -> found
                else -> current.pairTarget
            }
            current.copy(
                discovered = list,
                pairTarget = target,
                detail = if (known) current.detail else "发现 ${found.name}（${found.host}:${found.port}）。输入 6 位配对码后点连接。"
            )
        }
    }

    private fun holdMulticast(): Boolean {
        val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
        if (wifi == null) return false
        return try {
            multicast = wifi.createMulticastLock("lanbridge-discover").apply {
                setReferenceCounted(false)
                acquire()
            }
            @Suppress("DEPRECATION")
            wifiLock = wifi.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "lanbridge-wifi").apply {
                setReferenceCounted(false)
                acquire()
            }
            multicast?.isHeld == true
        } catch (_: Exception) {
            false
        }
    }

    private fun releaseMulticast() {
        try { if (multicast?.isHeld == true) multicast?.release() } catch (_: Exception) {}
        multicast = null
        try { if (wifiLock?.isHeld == true) wifiLock?.release() } catch (_: Exception) {}
        wifiLock = null
    }

    fun pair(host: String, port: Int, fingerprint: String, pin: String, remoteId: String?) {
        scope.launch {
            if (!pairing.compareAndSet(false, true)) return@launch
            try {
                reconnectJob?.cancel()
                socket?.cancel()
                socket = null
                update { it.copy(busy = "正在配对…", detail = "请在电脑上点允许。") }
                val result = doPair(host, port, fingerprint.lowercase(), pin, remoteId)
                update { it.copy(busy = null, detail = result) }
            } catch (ex: Exception) {
                update { it.copy(busy = null, detail = "配对失败：" + (ex.message ?: "网络错误")) }
            } finally {
                pairing.set(false)
                if (socket == null && peer != null && !stopped) scheduleReconnect(500)
            }
        }
    }

    fun pairLink(raw: String) {
        val link = Protocol.parsePairLink(raw)
        if (link == null) {
            update { it.copy(detail = "这不是兰桥的配对链接。") }
            return
        }
        if (link.expiresAtUnixSeconds < System.currentTimeMillis() / 1000) {
            update { it.copy(detail = "配对码已经过期，请在电脑上重新打开配对。") }
            return
        }
        pair(link.host, link.port, link.fingerprint, link.pin, link.deviceId)
    }

    fun sendText(text: String, manual: Boolean) {
        scope.launch { sendTextNow(text, manual) }
    }

    fun sendFiles(paths: List<String>) {
        scope.launch {
            for (path in paths) sendFileNow(File(path))
        }
    }

    fun acceptPending() {
        val pending = state.value.pendingFile ?: return
        update { it.copy(pendingFile = null) }
        scope.launch { download(pending.transferId, pending.name, pending.size, pending.sha256) }
    }

    fun declinePending() {
        val pending = state.value.pendingFile ?: return
        update { it.copy(pendingFile = null) }
        send(envelope("file.reject", JSONObject().put("transferId", pending.transferId).put("reason", "user_declined")))
    }

    private suspend fun doPair(host: String, port: Int, fingerprint: String, pin: String, remoteId: String?): String {
        var adopted = false
        try {
            return doPairAttempt(host, port, fingerprint, pin, remoteId).also { adopted = it.startsWith("已和") }
        } finally {
            if (!adopted) {
                val current = socket
                socket = null
                http = null
                current?.cancel()
            }
        }
    }

    private suspend fun doPairAttempt(host: String, port: Int, fingerprint: String, pin: String, remoteId: String?): String {
        var fp = fingerprint.lowercase()
        var remote = remoteId
        if (fp.length != 64) {
            val info = probe(host, port)
            fp = info.fingerprint
            if (remote.isNullOrBlank()) remote = info.deviceId.ifBlank { null }
        }
        val client = clientFor(fp)
        val waiter = CompletableDeferred<JSONObject>()
        pairWaiter = waiter
        val opened = CompletableDeferred<Unit>()
        val request = Request.Builder().url("wss://$host:$port/v1/ws").build()
        var failed: String? = null
        val ws = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                socket = webSocket
                http = client
                opened.complete(Unit)
            }
            override fun onMessage(webSocket: WebSocket, text: String) = dispatch(text)
            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) { webSocket.close(code, null) }
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) { if (socket === webSocket) onSocketGone() }
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                failed = t.message
                if (!opened.isCompleted) opened.complete(Unit)
                pairWaiter?.completeExceptionally(t)
                if (socket === webSocket) onSocketGone()
            }
        })
        withTimeoutOrNull(12_000) { opened.await() }
        if (socket == null) return "连不上 $host:$port。确认手机和电脑在同一个 Wi-Fi，且电脑上的兰桥正在配对。" + (failed?.let { "（$it）" } ?: "")
        val payload = JSONObject()
            .put("deviceId", prefs.deviceId())
            .put("deviceName", prefs.deviceName)
            .put("platform", "android")
            .put("publicKeySpki", Base64.encodeToString(identity.publicSpki(), Base64.NO_WRAP))
            .put("pin", pin)
            .put("protocol", Protocol.VERSION)
        ws.send(envelope("pair.request", payload))
        val first = withTimeoutOrNull(20_000) { waiter.await() } ?: return "电脑没有回应配对。"
        if (first.optString("type") == "pair.reject") return rejectText(first)
        if (first.optString("type") != "pair.pending") return "收到无法识别的配对回应。"
        val nextWaiter = CompletableDeferred<JSONObject>()
        pairWaiter = nextWaiter
        val second = withTimeoutOrNull(70_000) { nextWaiter.await() } ?: return "电脑没有在一分钟内确认。"
        if (second.optString("type") != "pair.accept") return rejectText(second)
        val accept = second.optJSONObject("payload") ?: JSONObject()
        val saved = PeerEntity(
            deviceId = accept.optString("deviceId").ifBlank { remote ?: UUID.randomUUID().toString() },
            name = accept.optString("deviceName").ifBlank { "电脑" },
            fingerprint = fp,
            host = host,
            port = port,
            pairedAt = System.currentTimeMillis()
        )
        db.peers().save(saved)
        peer = saved
        update { it.copy(status = "已连接 · ${saved.name}", online = true, peerName = saved.name, discovered = emptyList()) }
        announceHello()
        return "已和「${saved.name}」配对。之后文件和文字只发给这台电脑。"
    }

    private fun scheduleReconnect(initialDelay: Long) {
        if (stopped || pairing.get()) return
        reconnectJob?.cancel()
        reconnectJob = scope.launch {
            delay(initialDelay)
            var wait = 1_000L
            while (!stopped && !pairing.get()) {
                val saved = peer ?: db.peers().current()
                if (saved == null) break
                peer = saved
                update { it.copy(status = "正在连接 ${saved.name}…", online = false, peerName = saved.name) }
                val opened = openAuthed(saved)
                if (opened) {
                    update { it.copy(status = "已连接 · ${saved.name}", online = true, detail = "可以发送文字或文件。") }
                    return@launch
                }
                delay(wait)
                wait = (wait * 2).coerceAtMost(30_000)
            }
        }
    }

    private suspend fun openAuthed(saved: PeerEntity): Boolean {
        val client = clientFor(saved.fingerprint)
        val opened = CompletableDeferred<Boolean>()
        val path = "/v1/ws"
        val url = "wss://${saved.host}:${saved.port}$path"
        val request = signed("GET", url, path, null, "")
        client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                socket = webSocket
                http = client
                opened.complete(true)
                announceHello()
            }
            override fun onMessage(webSocket: WebSocket, text: String) = dispatch(text)
            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) { webSocket.close(code, null) }
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                if (socket === webSocket) onSocketGone()
            }
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (!opened.isCompleted) opened.complete(false)
                if (socket === webSocket) onSocketGone()
                update { it.copy(detail = "连接中断：" + (t.message ?: "网络错误") + "。会自动重试。") }
            }
        })
        return withTimeoutOrNull(12_000) { opened.await() } == true
    }

    private fun onSocketGone() {
        socket = null
        update { it.copy(online = false, status = if (peer == null) "未配对" else "未连接") }
        if (!stopped && !pairing.get() && peer != null) scheduleReconnect(1_000)
    }

    private fun announceHello() {
        send(envelope("hello", JSONObject()
            .put("deviceId", prefs.deviceId())
            .put("deviceName", prefs.deviceName)
            .put("protocol", Protocol.VERSION)
            .put("role", "phone")))
    }

    private fun dispatch(text: String) {
        val message = try { JSONObject(text) } catch (_: Exception) { return }
        val type = message.optString("type")
        val payload = message.optJSONObject("payload") ?: JSONObject()
        when (type) {
            "ping" -> send(envelope("pong", JSONObject()))
            "pair.pending", "pair.accept", "pair.reject" -> pairWaiter?.complete(message)
            "hello" -> {
                val name = payload.optString("deviceName")
                if (name.isNotBlank()) {
                    peer = peer?.copy(name = name)
                    update { it.copy(peerName = name, status = "已连接 · $name", online = true) }
                    scope.launch { peer?.let { db.peers().save(it) } }
                }
            }
            "device.revoked" -> scope.launch { forget() }
            "clipboard.ack" -> ackWaiters.remove(payload.optString("refId"))?.complete(message)
            "clipboard.push" -> scope.launch { incomingText(message.optString("msgId"), payload) }
            "file.accept", "file.reject", "file.pending", "file.complete", "file.error", "file.progress" ->
                fileWaiters.remove(payload.optString("transferId"))?.complete(message)
            "file.offer" -> scope.launch { incomingOffer(payload) }
        }
    }

    private suspend fun incomingText(msgId: String, payload: JSONObject) {
        val text = Protocol.normalize(payload.optString("text"))
        val manual = payload.optBoolean("manual")
        val hash = Protocol.sha256Hex(text)
        if (!hash.equals(payload.optString("sha256"), true) || Protocol.exceedsLimit(text)) {
            send(envelope("clipboard.ack", JSONObject().put("refId", msgId).put("ok", false).put("reason", "hash_mismatch")))
            return
        }
        val sensitive = Protocol.isSensitive(text)
        if (sensitive && prefs.filterSensitive && !manual) {
            send(envelope("clipboard.ack", JSONObject().put("refId", msgId).put("ok", false).put("reason", "sensitive")))
            update { it.copy(detail = "电脑发来一段疑似密码的文字，已丢弃。") }
            return
        }
        textToApply = text
        try {
            val manager = context.getSystemService(ClipboardManager::class.java)
            manager.setPrimaryClip(ClipData.newPlainText("兰桥", text))
        } catch (_: Exception) {}
        send(envelope("clipboard.ack", JSONObject().put("refId", msgId).put("ok", true)))
        notify("收到一段文字", if (sensitive) "已放入剪贴板，不写入历史。" else "已放入剪贴板。")
        update { it.copy(detail = if (sensitive) "收到一段敏感文字，已放入剪贴板，不写入历史。" else "收到一段文字，已放入剪贴板。") }
        if (prefs.saveHistory && !sensitive) remember("in", "clipboard", previewOf(text), text.toByteArray().size.toLong(), "completed", previewOf(text))
    }

    private suspend fun incomingOffer(payload: JSONObject) {
        if (payload.optString("direction") != "to_phone") return
        val id = payload.optString("transferId")
        val name = Protocol.sanitize(payload.optString("name"))
        val size = payload.optLong("size")
        val free = try { StatFs(context.cacheDir.absolutePath).availableBytes } catch (_: Exception) { Long.MAX_VALUE }
        val decision = Protocol.evaluateReceive(name, size, free, Long.MAX_VALUE, prefs.autoReceive, 0)
        if (decision.action == "reject") {
            send(envelope("file.reject", JSONObject().put("transferId", id).put("reason", decision.reason)))
            update { it.copy(detail = "没有接收「$name」：${decision.reason}") }
            return
        }
        if (decision.action == "confirm") {
            update { it.copy(pendingFile = PendingFile(id, decision.safeName, size, payload.optString("sha256")), detail = "电脑想发来「${decision.safeName}」。") }
            notify("有文件待接收", decision.safeName)
            return
        }
        download(id, decision.safeName, size, payload.optString("sha256"))
    }

    private suspend fun download(id: String, name: String, size: Long, expectedSha: String) {
        val saved = peer ?: return
        val client = http ?: return
        val path = "/v1/files/$id"
        val request = signed("GET", "https://${saved.host}:${saved.port}$path", path, null, "")
        update { it.copy(busy = "正在接收 $name") }
        try {
            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) {
                    send(envelope("file.error", JSONObject().put("transferId", id).put("reason", "http_${response.code}")))
                    update { it.copy(detail = "接收失败（${response.code}）。") }
                    return
                }
                val temp = File(context.cacheDir, "$id.part")
                response.body!!.byteStream().use { input -> temp.outputStream().use { output -> input.copyTo(output) } }
                val actual = temp.inputStream().use { Protocol.sha256Hex(it) }
                val header = response.header("X-LanBridge-Content-Sha256").orEmpty()
                val expect = expectedSha.ifBlank { header }
                if (expect.isNotBlank() && !actual.equals(expect, true)) {
                    temp.delete()
                    send(envelope("file.error", JSONObject().put("transferId", id).put("reason", "hash_mismatch")))
                    update { it.copy(detail = "「$name」校验不一致，已丢弃。") }
                    return
                }
                val uri = publishDownload(temp, name)
                temp.delete()
                send(envelope("file.complete", JSONObject().put("transferId", id).put("sha256", actual).put("name", name)))
                notify("文件已保存", "$name 在下载/LanBridge，没有打开。")
                update { it.copy(detail = "已保存「$name」。没有打开或运行它。") }
                remember("in", "file", name, size, "completed", uri?.toString())
            }
        } catch (ex: Exception) {
            update { it.copy(detail = "接收失败：" + (ex.message ?: "网络错误")) }
        } finally {
            update { it.copy(busy = null) }
        }
    }

    private fun publishDownload(source: File, name: String): android.net.Uri? {
        val values = android.content.ContentValues().apply {
            put(MediaStore.Downloads.DISPLAY_NAME, name)
            put(MediaStore.Downloads.MIME_TYPE, "application/octet-stream")
            put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/LanBridge")
            put(MediaStore.Downloads.IS_PENDING, 1)
        }
        val uri = context.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return null
        context.contentResolver.openOutputStream(uri)?.use { output -> source.inputStream().use { it.copyTo(output) } }
        values.clear()
        values.put(MediaStore.Downloads.IS_PENDING, 0)
        context.contentResolver.update(uri, values, null, null)
        return uri
    }

    private suspend fun sendTextNow(raw: String, manual: Boolean) {
        val text = Protocol.normalize(raw)
        if (text.isEmpty()) {
            update { it.copy(detail = "没有可发送的文字。") }
            return
        }
        if (Protocol.exceedsLimit(text)) {
            update { it.copy(detail = "文字超过 256 KB，没有发送。") }
            return
        }
        if (!manual && prefs.filterSensitive && Protocol.isSensitive(text)) {
            notify("没有自动发送", "这段内容像是密码或密钥。")
            return
        }
        if (socket == null) {
            update { it.copy(detail = "电脑还没连上。") }
            return
        }
        val hash = Protocol.sha256Hex(text)
        val env = JSONObject()
            .put("v", Protocol.VERSION)
            .put("type", "clipboard.push")
            .put("msgId", UUID.randomUUID().toString())
            .put("ts", System.currentTimeMillis())
            .put("payload", JSONObject().put("text", text).put("sha256", hash).put("manual", manual))
        val waiter = CompletableDeferred<JSONObject>()
        ackWaiters[env.getString("msgId")] = waiter
        socket?.send(env.toString())
        val ack = withTimeoutOrNull(5_000) { waiter.await() }
        val ok = ack?.optJSONObject("payload")?.optBoolean("ok") == true
        update { it.copy(detail = if (ok) "文字已发到电脑。" else "电脑没有收下这段文字。") }
        if (ok && prefs.saveHistory && !Protocol.isSensitive(text)) remember("out", "clipboard", previewOf(text), text.toByteArray().size.toLong(), "completed", previewOf(text))
    }

    private suspend fun sendFileNow(file: File) {
        if (!file.exists()) return
        val saved = peer
        val client = http
        if (saved == null || client == null || socket == null) {
            update { it.copy(detail = "电脑还没连上，文件留在本机缓存里。请先配对并连接。") }
            return
        }
        val name = Protocol.sanitize(file.name)
        val size = file.length()
        val hash = file.inputStream().use { Protocol.sha256Hex(it) }
        val id = UUID.randomUUID().toString()
        val waiter = CompletableDeferred<JSONObject>()
        fileWaiters[id] = waiter
        send(envelope("file.offer", JSONObject()
            .put("transferId", id)
            .put("name", name)
            .put("size", size)
            .put("mime", "application/octet-stream")
            .put("sha256", hash)
            .put("direction", "to_pc")))
        update { it.copy(busy = "正在发送 $name") }
        try {
            var reply = withTimeoutOrNull(60_000) { waiter.await() }
            if (reply?.optString("type") == "file.pending") {
                val again = CompletableDeferred<JSONObject>()
                fileWaiters[id] = again
                reply = withTimeoutOrNull(70_000) { again.await() }
            }
            if (reply?.optString("type") != "file.accept") {
                val reason = reply?.optJSONObject("payload")?.optString("reason") ?: "没有响应"
                update { it.copy(detail = "电脑没有接收「$name」（$reason）。") }
                remember("out", "file", name, size, "rejected", null)
                return
            }
            val code = if (size > Protocol.CHUNK_SIZE) putChunks(client, saved, id, file, hash) else putWhole(client, saved, id, file, hash)
            val ok = code in 200..299
            update { it.copy(detail = if (ok) "「$name」已发到电脑。" else "发送「$name」失败（$code）。") }
            remember("out", "file", name, size, if (ok) "completed" else "failed", null)
        } finally {
            if (file.absolutePath.startsWith(context.cacheDir.absolutePath)) file.delete()
            update { it.copy(busy = null) }
        }
    }

    private fun putWhole(client: OkHttpClient, saved: PeerEntity, id: String, file: File, hash: String): Int {
        val path = "/v1/files/$id"
        val body = object : RequestBody() {
            override fun contentType() = "application/octet-stream".toMediaType()
            override fun contentLength() = file.length()
            override fun writeTo(sink: BufferedSink) { file.source().use { sink.writeAll(it) } }
        }
        val request = signed("PUT", "https://${saved.host}:${saved.port}$path", path, body, hash)
        client.newCall(request).execute().use { return it.code }
    }

    private fun putChunks(client: OkHttpClient, saved: PeerEntity, id: String, file: File, @Suppress("UNUSED_PARAMETER") hash: String): Int {
        file.inputStream().use { input ->
            var index = 0
            while (true) {
                val buffer = ByteArray(Protocol.CHUNK_SIZE)
                val read = input.read(buffer)
                if (read < 0) break
                val slice = if (read == buffer.size) buffer else buffer.copyOf(read)
                val chunkHash = Protocol.sha256Hex(slice)
                val path = "/v1/files/$id/chunks/$index"
                val body = slice.toRequestBody("application/octet-stream".toMediaType())
                val request = signed("PUT", "https://${saved.host}:${saved.port}$path", path, body, chunkHash)
                val code = client.newCall(request).execute().use { it.code }
                if (code == 409 && index > 0) return code
                if (code !in 200..299) return code
                index++
            }
        }
        return 200
    }

    private fun signed(method: String, url: String, path: String, body: RequestBody?, contentSha: String): Request {
        val timestamp = System.currentTimeMillis()
        val nonce = Protocol.randomNonce()
        val canonical = Protocol.canonical(timestamp, nonce, method, path, contentSha)
        val signature = Base64.encodeToString(identity.sign(canonical), Base64.NO_WRAP)
        val builder = Request.Builder().url(url)
        when (method) {
            "GET" -> builder.get()
            else -> builder.method(method, body ?: ByteArray(0).toRequestBody(null))
        }
        builder.header("X-LanBridge-Device-Id", prefs.deviceId())
        builder.header("X-LanBridge-Timestamp", timestamp.toString())
        builder.header("X-LanBridge-Nonce", nonce)
        builder.header("X-LanBridge-Signature", signature)
        if (contentSha.isNotEmpty()) builder.header("X-LanBridge-Content-Sha256", contentSha)
        return builder.build()
    }

    private fun send(json: String): Boolean = socket?.send(json) == true

    private fun envelope(type: String, payload: JSONObject): String = JSONObject()
        .put("v", Protocol.VERSION)
        .put("type", type)
        .put("msgId", UUID.randomUUID().toString())
        .put("ts", System.currentTimeMillis())
        .put("payload", payload)
        .toString()

    private suspend fun remember(direction: String, kind: String, name: String, size: Long, status: String, preview: String?) {
        db.transfers().upsert(TransferEntity(
            id = UUID.randomUUID().toString(),
            direction = direction,
            kind = kind,
            name = name,
            size = size,
            status = status,
            createdAt = System.currentTimeMillis(),
            preview = preview
        ))
        refreshHistory()
    }

    private suspend fun refreshHistory() {
        val recent = db.transfers().recent()
        update { it.copy(transfers = recent) }
    }

    private fun publishSettings() {
        update {
            it.copy(
                deviceName = prefs.deviceName,
                autoReceive = prefs.autoReceive,
                saveHistory = prefs.saveHistory,
                filterSensitive = prefs.filterSensitive
            )
        }
    }

    private fun update(block: (PhoneState) -> PhoneState) { stateFlow.update(block) }

    private fun rejectText(message: JSONObject): String = when (message.optJSONObject("payload")?.optString("reason")) {
        "mismatch" -> "配对码不对。"
        "locked" -> "试错次数太多，请在电脑上重新打开配对。"
        "denied" -> "电脑没有允许这次配对。"
        "timeout" -> "电脑上的确认超时了。"
        "closed" -> "配对窗口已经关闭。"
        else -> "配对没有完成。"
    }

    private fun previewOf(text: String): String {
        val flat = text.replace('\n', ' ')
        return if (flat.length > 80) flat.substring(0, 80) + "…" else flat
    }

    private fun probe(host: String, port: Int): app.lanbridge.protocol.PcInfo {
        var certFp = ""
        val trust = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                val cert = chain?.firstOrNull() ?: throw CertificateException("empty")
                certFp = Protocol.sha256Hex(cert.encoded)
            }
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }
        val ssl = SSLContext.getInstance("TLS")
        ssl.init(null, arrayOf<javax.net.ssl.TrustManager>(trust), SecureRandom())
        val client = OkHttpClient.Builder()
            .sslSocketFactory(ssl.socketFactory, trust)
            .hostnameVerifier { _, _ -> true }
            .connectTimeout(6, TimeUnit.SECONDS)
            .readTimeout(6, TimeUnit.SECONDS)
            .build()
        val body = client.newCall(Request.Builder().url("https://$host:$port/v1/info").build()).execute().use { response ->
            if (!response.isSuccessful) error("电脑没有回应（${response.code}）")
            response.body?.string().orEmpty()
        }
        val info = PairingUi.parseInfo(body) ?: error("这个地址上不是兰桥")
        if (!info.fingerprint.equals(certFp, ignoreCase = true)) error("证书和宣告的指纹不一致")
        return info
    }

    private fun clientFor(fingerprint: String): OkHttpClient {
        val trust = FingerprintTrust(fingerprint)
        val ssl = SSLContext.getInstance("TLS")
        ssl.init(null, arrayOf<javax.net.ssl.TrustManager>(trust), SecureRandom())
        return OkHttpClient.Builder()
            .sslSocketFactory(ssl.socketFactory, trust)
            .hostnameVerifier { _, _ -> true }
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(0, TimeUnit.SECONDS)
            .writeTimeout(10, TimeUnit.MINUTES)
            .build()
    }

    private class FingerprintTrust(private val expected: String) : X509TrustManager {
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            val cert = chain?.firstOrNull() ?: throw CertificateException("empty")
            val actual = Protocol.sha256Hex(cert.encoded)
            if (!actual.equals(expected, ignoreCase = true)) throw CertificateException("fingerprint")
        }
        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    }

}
