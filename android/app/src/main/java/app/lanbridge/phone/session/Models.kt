package app.lanbridge.phone.session

import app.lanbridge.phone.data.TransferEntity

data class DiscoveredPc(
    val name: String,
    val host: String,
    val port: Int,
    val deviceId: String,
    val fingerprint: String
)

data class PendingFile(
    val transferId: String,
    val name: String,
    val size: Long,
    val sha256: String
)

data class PhoneState(
    val status: String = "正在启动…",
    val detail: String = "兰桥只在当前局域网里工作，不经过云端。",
    val online: Boolean = false,
    val peerName: String? = null,
    val transfers: List<TransferEntity> = emptyList(),
    val discovered: List<DiscoveredPc> = emptyList(),
    val pendingFile: PendingFile? = null,
    val deviceName: String = "",
    val autoReceive: Boolean = true,
    val saveHistory: Boolean = true,
    val filterSensitive: Boolean = true,
    val busy: String? = null
)
