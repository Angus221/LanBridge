package app.lanbridge.phone.share

import android.app.Activity
import android.content.ClipboardManager
import android.content.Intent
import android.os.Bundle
import android.view.WindowManager
import android.widget.Toast
import androidx.core.content.ContextCompat
import app.lanbridge.phone.service.BridgeService

/**
 * Comes to the foreground just long enough for Android to allow a clipboard read,
 * then sends that text to the selected PC. This is what makes a notification action
 * work after copying inside WeChat.
 */
class ClipboardSendActivity : Activity() {
    private var finishedSend = false
    private var attempts = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL)
        window.setLayout(1, 1)
    }

    override fun onResume() {
        super.onResume()
        window.decorView.post { tryRead() }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) window.decorView.post { tryRead() }
    }

    private fun tryRead() {
        if (finishedSend || isFinishing) return
        val text = readClip()
        if (text.isNotBlank()) {
            deliver(text)
            return
        }
        attempts += 1
        if (attempts < 4) {
            window.decorView.postDelayed({ tryRead() }, 90)
            return
        }
        deliver("")
    }

    private fun readClip(): String {
        val clip = getSystemService(ClipboardManager::class.java)?.primaryClip ?: return ""
        if (clip.itemCount == 0) return ""
        return clip.getItemAt(0)?.coerceToText(this)?.toString().orEmpty()
    }

    private fun deliver(text: String) {
        if (finishedSend) return
        finishedSend = true
        if (text.isBlank()) {
            Toast.makeText(this, "剪贴板是空的", Toast.LENGTH_SHORT).show()
        } else {
            val controller = BridgeService.controller
            val online = controller?.state?.value?.online == true
            if (controller != null) controller.sendText(text, true)
            else {
                ContextCompat.startForegroundService(this, Intent(this, BridgeService::class.java).apply {
                    action = BridgeService.ACTION_TEXT
                    putExtra(BridgeService.EXTRA_TEXT, text)
                    putExtra(BridgeService.EXTRA_MANUAL, true)
                })
            }
            Toast.makeText(this, if (online) "已发送剪贴板" else "电脑还没连上", Toast.LENGTH_SHORT).show()
        }
        finish()
        overridePendingTransition(0, 0)
    }
}
