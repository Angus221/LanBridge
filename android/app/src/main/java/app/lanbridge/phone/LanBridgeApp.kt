package app.lanbridge.phone

import android.app.Application
import android.content.Intent
import androidx.core.content.ContextCompat
import app.lanbridge.phone.service.BridgeService

class LanBridgeApp : Application() {
    override fun onCreate() {
        super.onCreate()
        ContextCompat.startForegroundService(this, Intent(this, BridgeService::class.java))
    }
}
