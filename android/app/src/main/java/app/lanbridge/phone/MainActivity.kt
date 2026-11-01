package app.lanbridge.phone

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.lifecycleScope
import app.lanbridge.phone.qr.QrScanActivity
import app.lanbridge.phone.service.BridgeService
import app.lanbridge.phone.session.DiscoveredPc
import app.lanbridge.phone.session.PhoneState
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private val scan = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val payload = result.data?.getStringExtra("payload") ?: return@registerForActivityResult
        BridgeService.controller?.pairLink(payload)
    }
    private val permissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= 33) {
            permissions.launch(arrayOf(Manifest.permission.POST_NOTIFICATIONS, Manifest.permission.NEARBY_WIFI_DEVICES))
        }
        setContent { LanBridgeTheme { HomeScreen(
            onScan = { scan.launch(Intent(this, QrScanActivity::class.java)) },
            onSendClipboard = { sendClipboard() }
        ) } }
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

    private fun sendClipboard() {
        val clip = getSystemService(ClipboardManager::class.java).primaryClip
        val text = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this)?.toString().orEmpty()
        if (text.isBlank()) return
        BridgeService.controller?.sendText(text, true)
    }
}

private val Ink = Color(0xFF1B3A4B)
private val Amber = Color(0xFFC47E12)
private val Paper = Color(0xFFF3EFE6)

@Composable
private fun LanBridgeTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = lightColorScheme(
            primary = Amber,
            onPrimary = Color.White,
            secondary = Ink,
            background = Paper,
            surface = Color(0xFFFFFCF8)
        ),
        content = content
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun HomeScreen(onScan: () -> Unit, onSendClipboard: () -> Unit) {
    var controller by remember { mutableStateOf(BridgeService.controller) }
    LaunchedEffect(Unit) {
        while (controller == null) {
            delay(200)
            controller = BridgeService.controller
        }
    }
    val ready = controller
    if (ready == null) {
        Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.Center) {
            Text("正在启动兰桥…", color = Ink, fontWeight = FontWeight.SemiBold)
            Text("如果一直停在这里，请从通知里重新打开。", color = Color(0xFF5C6B73), modifier = Modifier.padding(top = 8.dp))
        }
        return
    }
    val state by ready.state.collectAsStateWithLifecycle()
    var showPair by remember { mutableStateOf(false) }
    var showSettings by remember { mutableStateOf(false) }
    Scaffold(
        containerColor = Paper,
        topBar = {
            TopAppBar(
                title = { Text("兰桥") },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = Ink, titleContentColor = Color.White)
            )
        }
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding).padding(20.dp).verticalScroll(rememberScrollState())) {
            Text(state.status, color = Amber, fontWeight = FontWeight.SemiBold)
            Text(state.detail, color = Color(0xFF5C6B73), modifier = Modifier.padding(top = 6.dp, bottom = 16.dp))
            state.busy?.let { Text(it, color = Ink, modifier = Modifier.padding(bottom = 12.dp)) }
            state.pendingFile?.let { pending ->
                Card(Modifier.fillMaxWidth().padding(bottom = 12.dp)) {
                    Column(Modifier.padding(16.dp)) {
                        Text("接收「${pending.name}」？", fontWeight = FontWeight.SemiBold)
                        Text("文件只会保存到下载目录，不会被打开。", modifier = Modifier.padding(top = 4.dp, bottom = 8.dp))
                        Row {
                            Button(onClick = { ready.acceptPending() }) { Text("接收") }
                            OutlinedButton(onClick = { ready.declinePending() }, modifier = Modifier.padding(start = 8.dp)) { Text("拒绝") }
                        }
                    }
                }
            }
            Row {
                Button(onClick = onSendClipboard, enabled = state.online) { Text("发送剪贴板") }
                OutlinedButton(onClick = { showPair = !showPair }, modifier = Modifier.padding(start = 8.dp)) { Text(if (showPair) "收起配对" else "配对电脑") }
            }
            if (showPair) PairPanel(state, onScan)
            Text("最近传输", fontWeight = FontWeight.SemiBold, color = Ink, modifier = Modifier.padding(top = 20.dp, bottom = 8.dp))
            if (state.transfers.isEmpty()) {
                Text("还没有传输记录。配对之后，可以从分享菜单或这里把文字、文件发到电脑。", color = Color(0xFF5C6B73))
            } else {
                state.transfers.take(20).forEach { item ->
                    Text(item.name, modifier = Modifier.padding(top = 8.dp))
                    Text(
                        (if (item.direction == "out") "发出" else "收到") + " · " + (if (item.kind == "clipboard") "文字" else "文件") + " · " + item.status,
                        color = Color(0xFF5C6B73)
                    )
                }
            }
            TextButton(onClick = { showSettings = !showSettings }) { Text(if (showSettings) "收起设置" else "设置") }
            if (showSettings) SettingsPanel(state)
        }
    }
}

@Composable
private fun PairPanel(state: PhoneState, onScan: () -> Unit) {
    val controller = BridgeService.controller ?: return
    var pin by remember { mutableStateOf("") }
    var link by remember { mutableStateOf("") }
    var picked by remember { mutableStateOf<DiscoveredPc?>(state.discovered.firstOrNull()) }
    Column(Modifier.padding(top = 16.dp)) {
        Text("电脑和手机要在同一个 Wi-Fi。配对码 6 位，电脑上还会再问你一次。", color = Color(0xFF5C6B73))
        Button(onClick = onScan, modifier = Modifier.padding(top = 8.dp)) { Text("扫描二维码") }
        OutlinedButton(onClick = { controller.startDiscovery() }, modifier = Modifier.padding(top = 8.dp)) { Text("搜索同一局域网里的电脑") }
        state.discovered.forEach { pc ->
            TextButton(onClick = { picked = pc }) {
                Text((if (picked?.fingerprint == pc.fingerprint) "已选 · " else "") + pc.name + "  " + pc.host)
            }
        }
        OutlinedTextField(pin, { pin = it.take(6) }, label = { Text("6 位配对码") }, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), modifier = Modifier.fillMaxWidth().padding(top = 8.dp))
        Button(
            onClick = {
                val pc = picked ?: return@Button
                controller.pair(pc.host, pc.port, pc.fingerprint, pin, pc.deviceId)
            },
            enabled = pin.length == 6 && picked != null,
            modifier = Modifier.padding(top = 8.dp)
        ) { Text("用选中的电脑配对") }
        OutlinedTextField(link, { link = it }, label = { Text("或粘贴 lanbridge:// 链接") }, modifier = Modifier.fillMaxWidth().padding(top = 8.dp))
        OutlinedButton(onClick = { controller.pairLink(link.trim()) }, modifier = Modifier.padding(top = 8.dp)) { Text("用链接配对") }
    }
}

@Composable
private fun SettingsPanel(state: PhoneState) {
    val controller = BridgeService.controller ?: return
    var name by remember(state.deviceName) { mutableStateOf(state.deviceName) }
    Column {
        OutlinedTextField(name, { name = it }, label = { Text("这台手机的名称") }, modifier = Modifier.fillMaxWidth())
        Button(onClick = { controller.updateName(name) }, modifier = Modifier.padding(top = 8.dp)) { Text("保存名称") }
        CheckRow("自动接收电脑发来的文件", state.autoReceive) { controller.updateAutoReceive(it) }
        CheckRow("把文字传输记入历史（疑似密码不记）", state.saveHistory) { controller.updateSaveHistory(it) }
        CheckRow("拦截疑似密码、私钥和访问令牌", state.filterSensitive) { controller.updateFilter(it) }
        Text("Android 10 起，手机不能在后台读取剪贴板。请用上面的按钮、分享菜单、选中文字里的「发送到电脑」，或下拉快捷开关。", color = Color(0xFF5C6B73), modifier = Modifier.padding(top = 8.dp))
        OutlinedButton(onClick = { controller.forget() }, modifier = Modifier.padding(top = 8.dp)) { Text("解除这台电脑") }
        TextButton(onClick = { controller.clearHistory() }) { Text("清空传输记录") }
    }
}

@Composable
private fun CheckRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().padding(top = 4.dp)) {
        Checkbox(checked, onChange)
        Text(label, modifier = Modifier.padding(top = 12.dp))
    }
}
