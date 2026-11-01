package app.lanbridge.phone.share

import android.app.Activity
import android.content.ClipboardManager
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.widget.TextView
import android.widget.Toast
import androidx.core.content.ContextCompat
import app.lanbridge.phone.service.BridgeService
import java.io.File
import java.util.UUID

class SendActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val label = TextView(this)
        label.text = "正在准备发送…"
        label.setPadding(48, 48, 48, 48)
        setContentView(label)
        Thread {
            try {
                when (intent?.action) {
                    Intent.ACTION_PROCESS_TEXT -> sendText(intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString().orEmpty())
                    Intent.ACTION_SEND, Intent.ACTION_SEND_MULTIPLE -> {
                        val streams = streams(intent)
                        if (streams.isEmpty()) sendText(intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString().orEmpty())
                        else sendStreams(streams)
                    }
                    else -> {
                        val clip = getSystemService(ClipboardManager::class.java)?.primaryClip
                        val text = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this)?.toString().orEmpty()
                        sendText(text)
                    }
                }
            } catch (ex: Exception) {
                runOnUiThread { Toast.makeText(this, ex.message ?: "没有发出去", Toast.LENGTH_LONG).show() }
            } finally {
                runOnUiThread { finish() }
            }
        }.start()
    }

    private fun sendText(text: String) {
        if (text.isBlank()) {
            runOnUiThread { Toast.makeText(this, "没有可发送的文字", Toast.LENGTH_SHORT).show() }
            return
        }
        ContextCompat.startForegroundService(this, Intent(this, BridgeService::class.java).apply {
            action = BridgeService.ACTION_TEXT
            putExtra(BridgeService.EXTRA_TEXT, text)
            putExtra(BridgeService.EXTRA_MANUAL, true)
        })
        runOnUiThread { Toast.makeText(this, "正在发送到电脑", Toast.LENGTH_SHORT).show() }
    }

    private fun sendStreams(uris: List<Uri>) {
        val paths = ArrayList<String>()
        for (uri in uris) {
            val name = queryName(uri)
            val dest = File(cacheDir, UUID.randomUUID().toString() + "-" + name)
            contentResolver.openInputStream(uri)?.use { input -> dest.outputStream().use { input.copyTo(it) } } ?: continue
            paths.add(dest.absolutePath)
        }
        if (paths.isEmpty()) {
            runOnUiThread { Toast.makeText(this, "没有读到文件", Toast.LENGTH_SHORT).show() }
            return
        }
        ContextCompat.startForegroundService(this, Intent(this, BridgeService::class.java).apply {
            action = BridgeService.ACTION_FILES
            putStringArrayListExtra(BridgeService.EXTRA_PATHS, paths)
        })
        runOnUiThread { Toast.makeText(this, "正在发送 ${paths.size} 个文件", Toast.LENGTH_SHORT).show() }
    }

    private fun queryName(uri: Uri): String {
        val cursor = contentResolver.query(uri, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)
        cursor?.use {
            if (it.moveToFirst()) {
                val index = it.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
                if (index >= 0) return it.getString(index) ?: "received.bin"
            }
        }
        return uri.lastPathSegment ?: "received.bin"
    }

    private fun streams(intent: Intent): List<Uri> {
        if (intent.action == Intent.ACTION_SEND_MULTIPLE) {
            return if (Build.VERSION.SDK_INT >= 33) {
                intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java).orEmpty()
            } else {
                @Suppress("DEPRECATION")
                intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM).orEmpty()
            }
        }
        val one = if (Build.VERSION.SDK_INT >= 33) {
            intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
        } else {
            @Suppress("DEPRECATION")
            intent.getParcelableExtra(Intent.EXTRA_STREAM)
        }
        return listOfNotNull(one)
    }
}
