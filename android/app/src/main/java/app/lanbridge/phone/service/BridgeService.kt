package app.lanbridge.phone.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.drawable.Icon
import android.os.IBinder
import app.lanbridge.phone.MainActivity
import app.lanbridge.phone.R
import app.lanbridge.phone.session.PhoneController
import app.lanbridge.phone.share.ClipboardSendActivity

class BridgeService : Service() {
    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        ensureChannel()
        startForeground(NOTIFICATION_ID, notification("兰桥", "正在连接已配对的电脑"), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        val created = try {
            PhoneController(this) { title, body ->
                notify(title, body)
            }
        } catch (ex: Exception) {
            notify("兰桥没有启动", ex.message ?: "无法生成手机密钥")
            stopSelf()
            return
        }
        controller = created
        created.start()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val current = controller
        when (intent?.action) {
            ACTION_TEXT -> {
                val text = intent.getStringExtra(EXTRA_TEXT).orEmpty()
                current?.sendText(text, intent.getBooleanExtra(EXTRA_MANUAL, true))
            }
            ACTION_FILES -> {
                val paths = intent.getStringArrayListExtra(EXTRA_PATHS).orEmpty()
                current?.sendFiles(paths)
            }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        controller?.stop()
        controller = null
        super.onDestroy()
    }

    private fun notify(title: String, body: String) {
        val manager = getSystemService(NotificationManager::class.java)
        val notice = notification(title, body)
        manager.notify(NOTIFICATION_ID, notice)
        if (android.os.Build.VERSION.SDK_INT >= 34) {
            startForeground(NOTIFICATION_ID, notice, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(NOTIFICATION_ID, notice)
        }
    }

    private fun notification(title: String, body: String): Notification {
        val open = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val send = PendingIntent.getActivity(
            this,
            7,
            Intent(this, ClipboardSendActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_launcher)
            .setContentTitle(title)
            .setContentText(body)
            .setContentIntent(open)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setAutoCancel(false)
            .addAction(Notification.Action.Builder(Icon.createWithResource(this, R.drawable.ic_launcher), "发送剪贴板", send).build())
            .build()
    }

    private fun ensureChannel() {
        val manager = getSystemService(NotificationManager::class.java)
        val channel = NotificationChannel(CHANNEL, "兰桥", NotificationManager.IMPORTANCE_LOW)
        channel.description = "保持连接，并提供发送剪贴板按钮"
        channel.setShowBadge(false)
        manager.createNotificationChannel(channel)
    }

    companion object {
        const val CHANNEL = "lanbridge.persistent"
        const val NOTIFICATION_ID = 42
        const val ACTION_TEXT = "app.lanbridge.phone.TEXT"
        const val ACTION_FILES = "app.lanbridge.phone.FILES"
        const val EXTRA_TEXT = "text"
        const val EXTRA_MANUAL = "manual"
        const val EXTRA_PATHS = "paths"
        @Volatile var controller: PhoneController? = null
    }
}
