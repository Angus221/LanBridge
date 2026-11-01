package app.lanbridge.phone

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.view.WindowCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import app.lanbridge.phone.data.PeerEntity
import app.lanbridge.phone.data.TransferEntity
import app.lanbridge.phone.qr.QrScanActivity
import app.lanbridge.phone.service.BridgeService
import app.lanbridge.phone.session.PhoneState
import app.lanbridge.protocol.HistoryUi
import app.lanbridge.protocol.PairingUi
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.io.File
import java.util.UUID

class MainActivity : ComponentActivity() {
    private val scan = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val payload = result.data?.getStringExtra("payload") ?: return@registerForActivityResult
        BridgeService.controller?.applyQr(payload)
    }
    private val discoverPermissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        BridgeService.controller?.startDiscovery()
    }
    private val permissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { }
    private val photos = registerForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(10)) { uris ->
        sendUris(uris)
    }
    private val documents = registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        sendUris(uris)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        if (Build.VERSION.SDK_INT >= 33) {
            permissions.launch(arrayOf(Manifest.permission.POST_NOTIFICATIONS, Manifest.permission.NEARBY_WIFI_DEVICES))
        }
        setContent {
            LanBridgeTheme {
                HomeScreen(
                    onScan = { scan.launch(Intent(this, QrScanActivity::class.java)) },
                    onSearch = { requestDiscovery() },
                    onSendClipboard = { sendClipboard() },
                    onPickPhotos = { photos.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
                    onPickFiles = { documents.launch(arrayOf("*/*")) },
                    onCopy = { text -> copyText(text) },
                    onOpen = { uri -> openSaved(uri) }
                )
            }
        }
    }

    override fun onResume() {
        super.onResume()
        lifecycleScope.launch {
            delay(300)
            val text = BridgeService.controller?.textToApply ?: return@launch
            BridgeService.controller?.textToApply = null
            val manager = getSystemService(ClipboardManager::class.java)
            manager.setPrimaryClip(ClipData.newPlainText("兰桥", text))
        }
    }

    private fun requestDiscovery() {
        val needed = discoveryPermissions().filter {
            checkSelfPermission(it) != android.content.pm.PackageManager.PERMISSION_GRANTED
        }
        if (needed.isEmpty()) BridgeService.controller?.startDiscovery()
        else discoverPermissions.launch(needed.toTypedArray())
    }

    private fun sendClipboard() {
        val clip = getSystemService(ClipboardManager::class.java).primaryClip
        val text = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this)?.toString().orEmpty()
        if (text.isBlank()) {
            Toast.makeText(this, "剪贴板是空的", Toast.LENGTH_SHORT).show()
            return
        }
        BridgeService.controller?.sendText(text, true)
        Toast.makeText(this, "已发送剪贴板", Toast.LENGTH_SHORT).show()
    }

    private fun copyText(text: String) {
        getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("兰桥", text))
        Toast.makeText(this, "已复制", Toast.LENGTH_SHORT).show()
    }

    private fun openSaved(uri: String) {
        try {
            startActivity(Intent(Intent.ACTION_VIEW).apply {
                setData(Uri.parse(uri))
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            })
        } catch (ex: Exception) {
            Toast.makeText(this, "没有能打开这个文件的应用", Toast.LENGTH_SHORT).show()
        }
    }

    private fun sendUris(uris: List<Uri>) {
        if (uris.isEmpty()) return
        val paths = ArrayList<String>()
        for (uri in uris) {
            val name = queryName(uri)
            val dest = File(cacheDir, UUID.randomUUID().toString() + "-" + name)
            contentResolver.openInputStream(uri)?.use { input -> dest.outputStream().use { input.copyTo(it) } } ?: continue
            paths.add(dest.absolutePath)
        }
        if (paths.isEmpty()) {
            Toast.makeText(this, "没有读到文件", Toast.LENGTH_SHORT).show()
            return
        }
        BridgeService.controller?.sendFiles(paths)
        Toast.makeText(this, "正在发送 ${paths.size} 个文件", Toast.LENGTH_SHORT).show()
    }

    private fun queryName(uri: Uri): String {
        val cursor = contentResolver.query(uri, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)
        cursor?.use {
            if (it.moveToFirst()) {
                val name = it.getString(0)
                if (!name.isNullOrBlank()) return name.replace(Regex("[\\\\/]"), "_")
            }
        }
        return "file"
    }

    private fun discoveryPermissions(): Array<String> =
        if (Build.VERSION.SDK_INT >= 33) arrayOf(Manifest.permission.NEARBY_WIFI_DEVICES)
        else arrayOf(Manifest.permission.ACCESS_FINE_LOCATION)
}

private val AppleBlue = Color(0xFF0058C6)
private val AppleBlueDark = Color(0xFF409CFF)
private val AppleGreen = Color(0xFF1F7A34)
private val AppleGreenDark = Color(0xFF30D158)
private val Label = Color(0xFF1C1C1E)
private val Muted = Color(0xFF636366)
private val Grouped = Color(0xFFF2F2F7)

@Composable
private fun LanBridgeTheme(content: @Composable () -> Unit) {
    val dark = isSystemInDarkTheme()
    val scheme = if (dark) {
        darkColorScheme(
            primary = AppleBlueDark,
            onPrimary = Color.Black,
            secondary = AppleGreenDark,
            background = Color.Black,
            surface = Color(0xFF1C1C1E),
            onBackground = Color.White,
            onSurface = Color.White,
            onSurfaceVariant = Color(0xFFAEAEB2)
        )
    } else {
        lightColorScheme(
            primary = AppleBlue,
            onPrimary = Color.White,
            secondary = AppleGreen,
            background = Grouped,
            surface = Color.White,
            onBackground = Label,
            onSurface = Label,
            onSurfaceVariant = Muted
        )
    }
    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            val window = (view.context as ComponentActivity).window
            WindowCompat.getInsetsController(window, view).isAppearanceLightStatusBars = !dark
        }
    }
    MaterialTheme(colorScheme = scheme, content = content)
}

@Composable
private fun HomeScreen(
    onScan: () -> Unit,
    onSearch: () -> Unit,
    onSendClipboard: () -> Unit,
    onPickPhotos: () -> Unit,
    onPickFiles: () -> Unit,
    onCopy: (String) -> Unit,
    onOpen: (String) -> Unit
) {
    var controller by remember { mutableStateOf(BridgeService.controller) }
    LaunchedEffect(Unit) {
        while (controller == null) {
            delay(200)
            controller = BridgeService.controller
        }
    }
    val ready = controller
    val colors = MaterialTheme.colorScheme
    if (ready == null) {
        Column(Modifier.fillMaxSize().background(colors.background).padding(28.dp), verticalArrangement = Arrangement.Center) {
            Text("正在启动兰桥", color = colors.onBackground, fontWeight = FontWeight.SemiBold, fontSize = 22.sp)
            Text("如果一直停在这里，请从通知里重新打开。", color = colors.onSurfaceVariant, modifier = Modifier.padding(top = 8.dp))
        }
        return
    }
    val state by ready.state.collectAsStateWithLifecycle()
    var showPair by remember { mutableStateOf(false) }
    var showSettings by remember { mutableStateOf(false) }
    if (showSettings) {
        SettingsScreen(state, onClose = { showSettings = false })
        return
    }
    Column(Modifier.fillMaxSize().background(colors.background)) {
        Row(Modifier.fillMaxWidth().padding(start = 8.dp, end = 16.dp, top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier.size(48.dp).clip(RoundedCornerShape(14.dp)).semantics { contentDescription = "设置" }.clickable { showSettings = true },
                contentAlignment = Alignment.Center
            ) { GearIcon(colors.onBackground) }
            Text("兰桥", color = colors.onBackground, fontSize = 22.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(start = 4.dp))
        }
        LazyColumn(
            modifier = Modifier.weight(1f).fillMaxWidth(),
            contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 20.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            item {
                val selected = state.peers.firstOrNull { it.deviceId == state.selectedPeerId }
                PcHero(selected, state.online, state.status, state.detail)
            }
            state.busy?.let { busy -> item { Text(busy, color = colors.primary) } }
            state.pendingFile?.let { pending ->
                item {
                    Surface(shape = RoundedCornerShape(20.dp), color = colors.surface, shadowElevation = 1.dp, modifier = Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(16.dp)) {
                            Text("接收「${pending.name}」？", fontWeight = FontWeight.SemiBold, color = colors.onSurface)
                            Text("文件只会保存到下载目录，不会被打开。", color = colors.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp, bottom = 8.dp))
                            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                Button(onClick = { ready.acceptPending() }, modifier = Modifier.heightIn(min = 48.dp)) { Text("接收") }
                                OutlinedButton(onClick = { ready.declinePending() }, modifier = Modifier.heightIn(min = 48.dp)) { Text("拒绝") }
                            }
                        }
                    }
                }
            }
            item {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = onSendClipboard, enabled = state.online, modifier = Modifier.heightIn(min = 48.dp)) { Text("发送剪贴板") }
                    OutlinedButton(onClick = onPickPhotos, enabled = state.online, modifier = Modifier.heightIn(min = 48.dp)) { Text("照片") }
                    OutlinedButton(onClick = onPickFiles, enabled = state.online, modifier = Modifier.heightIn(min = 48.dp)) { Text("文件") }
                }
            }
            if (showPair) item { PairPanel(state, onScan, onSearch, onClose = { showPair = false }) }
            item {
                Text("传输记录", color = colors.onBackground, fontWeight = FontWeight.SemiBold, fontSize = 17.sp, modifier = Modifier.padding(top = 8.dp))
            }
            if (state.transfers.isEmpty()) {
                item { Text("这台电脑还没有记录。文字可以复制，文件可以打开。", color = colors.onSurfaceVariant) }
            } else {
                items(state.transfers, key = { it.id }) { item ->
                    HistoryCard(item, onCopy, onOpen)
                }
            }
        }
        PcBar(state.peers, state.selectedPeerId, onSelect = { ready.selectPeer(it) }, onAdd = { showPair = true })
    }
}

@Composable
private fun PcHero(peer: PeerEntity?, online: Boolean, status: String, detail: String) {
    val colors = MaterialTheme.colorScheme
    Surface(shape = RoundedCornerShape(22.dp), color = colors.surface, shadowElevation = 2.dp, modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(20.dp), verticalAlignment = Alignment.CenterVertically) {
            ComputerIcon(colors.primary)
            Column(Modifier.padding(start = 16.dp)) {
                Text(peer?.name ?: "还没有电脑", color = colors.onSurface, fontSize = 20.sp, fontWeight = FontWeight.SemiBold)
                Text(
                    if (peer == null) "未配对" else if (online) "已连接" else "未连接",
                    color = if (online) colors.primary else colors.onSurfaceVariant,
                    modifier = Modifier.padding(top = 4.dp)
                )
                Text(status, color = colors.onSurfaceVariant, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp))
                if (detail.isNotBlank()) {
                    Text(detail, color = colors.onSurfaceVariant, fontSize = 13.sp, modifier = Modifier.padding(top = 2.dp))
                }
            }
        }
    }
}

@Composable
private fun HistoryCard(item: TransferEntity, onCopy: (String) -> Unit, onOpen: (String) -> Unit) {
    val colors = MaterialTheme.colorScheme
    val fromPc = HistoryUi.originOnPhone(item.direction) == "来自电脑"
    Surface(shape = RoundedCornerShape(18.dp), color = colors.surface, shadowElevation = 1.dp, modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(start = 16.dp, end = 8.dp, top = 12.dp, bottom = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(
                    HistoryUi.originOnPhone(item.direction),
                    color = if (fromPc) colors.primary else colors.secondary,
                    fontWeight = FontWeight.SemiBold,
                    fontSize = 13.sp
                )
                Text(item.name, color = colors.onSurface, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 4.dp))
                Text(
                    HistoryUi.formatWhen(item.createdAt) + " · " + if (item.kind == "clipboard") "文字" else "文件",
                    color = colors.onSurfaceVariant,
                    fontSize = 12.sp,
                    modifier = Modifier.padding(top = 4.dp)
                )
            }
            if (HistoryUi.canCopy(item.kind) && !item.preview.isNullOrBlank()) {
                TextButton(onClick = { onCopy(item.preview) }, modifier = Modifier.heightIn(min = 48.dp)) { Text("复制") }
            } else if (HistoryUi.canOpen(item.kind, item.preview)) {
                TextButton(onClick = { onOpen(item.preview!!) }, modifier = Modifier.heightIn(min = 48.dp)) { Text("打开") }
            }
        }
    }
}

@Composable
private fun PcBar(peers: List<PeerEntity>, selectedId: String?, onSelect: (String) -> Unit, onAdd: () -> Unit) {
    val colors = MaterialTheme.colorScheme
    Surface(color = colors.surface, shadowElevation = 8.dp) {
        Row(
            Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 12.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            peers.forEach { peer ->
                val selected = peer.deviceId == selectedId
                Box(
                    Modifier.heightIn(min = 48.dp).clip(RoundedCornerShape(16.dp))
                        .background(if (selected) colors.primary else colors.background)
                        .clickable { onSelect(peer.deviceId) }
                        .padding(horizontal = 16.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Text(peer.name, color = if (selected) colors.onPrimary else colors.onBackground, maxLines = 1)
                }
            }
            Box(
                Modifier.heightIn(min = 48.dp).clip(RoundedCornerShape(16.dp))
                    .background(colors.background)
                    .clickable(onClick = onAdd)
                    .padding(horizontal = 16.dp),
                contentAlignment = Alignment.Center
            ) { Text("添加电脑", color = colors.primary, fontWeight = FontWeight.SemiBold) }
        }
    }
}

@Composable
private fun SettingsScreen(state: PhoneState, onClose: () -> Unit) {
    val controller = BridgeService.controller
    val colors = MaterialTheme.colorScheme
    var name by remember(state.deviceName) { mutableStateOf(state.deviceName) }
    Column(Modifier.fillMaxSize().background(colors.background).padding(horizontal = 20.dp, vertical = 12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onClose, modifier = Modifier.heightIn(min = 48.dp)) { Text("完成") }
            Text("设置", fontSize = 22.sp, fontWeight = FontWeight.SemiBold, color = colors.onBackground)
        }
        Spacer(Modifier.height(12.dp))
        OutlinedTextField(name, { name = it }, label = { Text("这台手机的名称") }, modifier = Modifier.fillMaxWidth())
        Button(onClick = { controller?.updateName(name) }, modifier = Modifier.padding(top = 8.dp).heightIn(min = 48.dp)) { Text("保存名称") }
        CheckRow("自动接收电脑发来的文件", state.autoReceive) { controller?.updateAutoReceive(it) }
        CheckRow("把文字传输记入历史（疑似密码不记）", state.saveHistory) { controller?.updateSaveHistory(it) }
        CheckRow("拦截疑似密码、私钥和访问令牌", state.filterSensitive) { controller?.updateFilter(it) }
        Text(
            "通知里的「发送剪贴板」和快捷设置开关会短暂把兰桥带到前台读取剪贴板，因此刚在微信里复制的文字也能发出去。",
            color = colors.onSurfaceVariant,
            modifier = Modifier.padding(top = 12.dp)
        )
        OutlinedButton(
            onClick = { controller?.forget(state.selectedPeerId) },
            modifier = Modifier.padding(top = 16.dp).heightIn(min = 48.dp)
        ) { Text("解除当前电脑") }
        TextButton(onClick = { controller?.clearHistory() }) { Text("清空传输记录") }
    }
}

@Composable
private fun PairPanel(state: PhoneState, onScan: () -> Unit, onSearch: () -> Unit, onClose: () -> Unit) {
    val controller = BridgeService.controller ?: return
    val colors = MaterialTheme.colorScheme
    var pin by remember { mutableStateOf("") }
    var manual by remember { mutableStateOf("") }
    var link by remember { mutableStateOf("") }
    LaunchedEffect(state.suggestedPin) {
        if (state.suggestedPin.length == 6) pin = state.suggestedPin
    }
    val manualPending = manual.isNotBlank() && PairingUi.parseManualEndpoint(manual) == null
    val hasTarget = state.pairTarget != null && !manualPending
    val reason = if (manualPending) {
        "请输入完整的局域网 IP，例如 192.168.1.20:47821。没写端口时默认 47821。"
    } else {
        PairingUi.blockReason(hasTarget, pin)
    }
    Surface(shape = RoundedCornerShape(20.dp), color = colors.surface, shadowElevation = 1.dp, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("添加电脑", fontWeight = FontWeight.SemiBold, color = colors.onSurface, modifier = Modifier.weight(1f))
                TextButton(onClick = onClose) { Text("收起") }
            }
            Text("电脑和手机要在同一个 Wi-Fi。配对码 6 位，电脑上还会再问你一次。", color = colors.onSurfaceVariant)
            Button(onClick = onScan, modifier = Modifier.padding(top = 8.dp).heightIn(min = 48.dp)) { Text("扫描二维码") }
            OutlinedButton(onClick = onSearch, modifier = Modifier.padding(top = 8.dp).heightIn(min = 48.dp)) { Text("搜索同一局域网里的电脑") }
            state.discovered.forEach { pc ->
                val selected = state.pairTarget?.host == pc.host && state.pairTarget?.port == pc.port && !manualPending
                TextButton(onClick = {
                    manual = ""
                    controller.chooseDiscovered(pc)
                }, modifier = Modifier.heightIn(min = 48.dp)) {
                    Text((if (selected) "已选 · " else "") + pc.name + "  " + pc.host + ":" + pc.port)
                }
            }
            OutlinedTextField(manual, { manual = it; controller.setManualEndpoint(it) }, label = { Text("或手动输入 IP:端口") }, modifier = Modifier.fillMaxWidth().padding(top = 8.dp))
            OutlinedTextField(
                pin,
                { pin = PairingUi.pinDigits(it).take(6) },
                label = { Text("6 位配对码") },
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                modifier = Modifier.fillMaxWidth().padding(top = 8.dp)
            )
            OutlinedTextField(link, { link = it }, label = { Text("或粘贴 lanbridge:// 链接") }, modifier = Modifier.fillMaxWidth().padding(top = 8.dp))
            TextButton(onClick = { controller.applyQr(link.trim()) }) { Text("使用这个链接里的电脑") }
            state.pairTarget?.takeIf { !manualPending }?.let { target ->
                Text("当前目标：${target.name}  ${target.host}:${target.port}", color = colors.onSurface, modifier = Modifier.padding(top = 8.dp))
            }
            if (reason != null) Text(reason, color = colors.primary, modifier = Modifier.padding(top = 8.dp))
            Button(
                onClick = {
                    val pc = state.pairTarget ?: return@Button
                    if (!PairingUi.canConnect(true, pin) || manualPending) return@Button
                    controller.pair(pc.host, pc.port, pc.fingerprint, PairingUi.pinDigits(pin), pc.deviceId.ifBlank { null })
                },
                enabled = reason == null,
                modifier = Modifier.padding(top = 8.dp).heightIn(min = 48.dp)
            ) { Text("连接") }
        }
    }
}

@Composable
private fun CheckRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Checkbox(checked, onChange)
        Text(label, modifier = Modifier.padding(start = 8.dp), color = MaterialTheme.colorScheme.onBackground)
    }
}

@Composable
private fun GearIcon(color: Color) {
    Canvas(Modifier.size(22.dp)) {
        val stroke = Stroke(width = 1.8.dp.toPx())
        drawCircle(color = color, radius = size.minDimension * 0.18f, style = stroke)
        drawCircle(color = color, radius = size.minDimension * 0.42f, style = stroke)
    }
}

@Composable
private fun ComputerIcon(color: Color) {
    Canvas(Modifier.size(44.dp)) {
        val stroke = Stroke(width = 2.dp.toPx())
        val screen = Path().apply {
            addRoundRect(androidx.compose.ui.geometry.RoundRect(4.dp.toPx(), 4.dp.toPx(), size.width - 4.dp.toPx(), size.height * 0.68f, 6.dp.toPx(), 6.dp.toPx()))
        }
        drawPath(screen, color, style = stroke)
        drawLine(color, androidx.compose.ui.geometry.Offset(size.width * 0.35f, size.height * 0.82f), androidx.compose.ui.geometry.Offset(size.width * 0.65f, size.height * 0.82f), strokeWidth = 2.dp.toPx())
        drawLine(color, androidx.compose.ui.geometry.Offset(size.width / 2f, size.height * 0.68f), androidx.compose.ui.geometry.Offset(size.width / 2f, size.height * 0.82f), strokeWidth = 2.dp.toPx())
    }
}
