package app.lanbridge.protocol

import java.util.Calendar

object HistoryUi {
    fun originOnPhone(direction: String): String = when (direction) {
        "in", "from_pc", "to_phone" -> "来自电脑"
        else -> "来自手机"
    }

    fun formatWhen(epochMs: Long): String {
        val calendar = Calendar.getInstance()
        calendar.timeInMillis = epochMs
        val month = calendar.get(Calendar.MONTH) + 1
        val day = calendar.get(Calendar.DAY_OF_MONTH)
        val hour = calendar.get(Calendar.HOUR_OF_DAY)
        val minute = calendar.get(Calendar.MINUTE)
        return "${month}月${day}日 %02d:%02d".format(hour, minute)
    }

    fun visible(peerId: String?, selectedPeerId: String?, peerCount: Int): Boolean {
        if (selectedPeerId.isNullOrEmpty()) return peerCount == 0 && peerId.isNullOrEmpty()
        if (peerId == selectedPeerId) return true
        return peerId.isNullOrEmpty() && peerCount <= 1
    }

    fun canCopy(kind: String): Boolean = kind == "clipboard" || kind == "text"

    fun canOpen(kind: String, preview: String?): Boolean =
        kind == "file" && preview?.startsWith("content:") == true
}
